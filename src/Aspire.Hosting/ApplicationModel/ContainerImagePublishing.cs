// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Registers remote source preparation and verified, registry-scoped image publication.
/// </summary>
internal static class ContainerImagePublishing
{
    internal static void Configure(ContainerImageResource source)
    {
        source.Annotations.Add(new PipelineStepAnnotation(factory =>
        {
            if (factory.PipelineContext.ExecutionContext.IsRunMode || source.IsExcludedFromPublish())
            {
                return [];
            }

            var destinations = source.GetPublications()
                .Select(publication => publication.Destination)
                .Where(destination => !destination.IsExcludedFromPublish())
                .ToArray();
            if (destinations.Length == 0)
            {
                return [];
            }

            foreach (var destination in destinations)
            {
                if (!factory.PipelineContext.Model.Resources.Contains(destination) ||
                    !factory.PipelineContext.Model.Resources.Any(resource =>
                        StringComparers.ResourceName.Equals(resource.Name, destination.Parent.Name) &&
                        resource is IContainerRegistry && !resource.IsExcludedFromPublish()))
                {
                    throw new DistributedApplicationException(
                        $"Destination image '{destination.Name}' and its registry '{destination.Parent.Name}' must be present in the publishable application model.");
                }
            }

            var configuration = source.GetSource();
            // A new closure is created on each pipeline resolution. Never reuse prepared content
            // across deployments: a mutable tag may move, or a previous preparation may have failed.
            PreparedImage? prepared = null;
            var prepareName = $"prepare-image-{source.Name}";
            var steps = new List<PipelineStep>
            {
                new()
                {
                    Name = prepareName,
                    Description = $"Resolves the remote image source for {source.Name}.",
                    Resource = source,
                    DependsOnSteps = [WellKnownPipelineSteps.DeployPrereq, WellKnownPipelineSteps.PushPrereq],
                    Action = async context =>
                    {
                        foreach (var destination in destinations)
                        {
                            destination.ClearPublishedDigest();
                        }
                        EnsureConfiguration(source, configuration);
                        var task = await context.ReportingStep.CreateTaskAsync(
                            new MarkdownString($"Preparing image **{source.Name}**"),
                            context.CancellationToken).ConfigureAwait(false);
                        await using var taskLifetime = task.ConfigureAwait(false);
                        try
                        {
                            var runtime = await context.Services.GetRequiredService<IContainerRuntimeResolver>()
                                .ResolveAsync(context.CancellationToken).ConfigureAwait(false);
                            var reference = await runtime.ResolveRemoteImageAsync(configuration.Image, context.CancellationToken).ConfigureAwait(false);
                            var resolved = ContainerImageName.ParseSource(reference);
                            if (resolved.Digest is null ||
                                !StringComparer.Ordinal.Equals(reference, $"{configuration.Source.Registry}/{configuration.Source.Image}@{resolved.Digest}") ||
                                (configuration.Source.Digest is { } expected && !StringComparer.Ordinal.Equals(expected, resolved.Digest)))
                            {
                                throw new DistributedApplicationException($"Container runtime returned an invalid immutable source reference for image '{source.Name}'.");
                            }
                            context.CancellationToken.ThrowIfCancellationRequested();
                            EnsureConfiguration(source, configuration);
                            // Tags are retained transport addresses, not consumer identities. A per-execution
                            // suffix prevents concurrent deployments from overwriting each other's verification tag.
                            prepared = new(runtime, reference, resolved.Digest, $"aspire-deploy-{Guid.NewGuid():N}");
                            await task.CompleteAsync(new MarkdownString($"Prepared **{source.Name}** at `{reference}`"),
                                CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            await task.FailAsync(new MarkdownString($"Failed to prepare **{source.Name}**: {ex.Message}"),
                                CancellationToken.None).ConfigureAwait(false);
                            throw;
                        }
                    }
                }
            };
            foreach (var destination in destinations)
            {
                steps.Add(new PipelineStep
                {
                    Name = $"push-{destination.Name}",
                    Description = $"Publishes {source.Name} to the {destination.Parent.Name} registry as {destination.Name}.",
                    Resource = destination,
                    Tags = [WellKnownPipelineTags.PushContainerImage],
                    DependsOnSteps = [prepareName],
                    // Standalone images must be reachable even without a compute deployment target.
                    RequiredBySteps = [WellKnownPipelineSteps.Push, WellKnownPipelineSteps.Deploy],
                    Action = context => PublishAsync(destination, configuration,
                        prepared ?? throw new DistributedApplicationException($"Image '{source.Name}' has not been prepared."),
                        context)
                });
            }

            return steps;
        }));

        source.Annotations.Add(new PipelineConfigurationAnnotation(context =>
        {
            foreach (var destination in source.GetPublications().Select(publication => publication.Destination))
            {
                context.GetSteps(destination, WellKnownPipelineTags.PushContainerImage)
                    .DependsOn(context.GetSteps(destination.Parent, WellKnownPipelineTags.ProvisionInfrastructure));
            }
        }));
    }

    private static async Task PublishAsync(
        DestinationImageResource destination,
        ContainerImageSourceAnnotation configuration,
        PreparedImage prepared,
        PipelineStepContext context)
    {
        EnsureConfiguration(destination.Source, configuration);
        destination.EnsureCurrentPublication();
        var task = await context.ReportingStep.CreateTaskAsync(
            new MarkdownString($"Pushing image **{destination.Name}** to **{destination.Parent.Name}**"),
            context.CancellationToken).ConfigureAwait(false);
        await using var taskLifetime = task.ConfigureAwait(false);
        try
        {
            var repository = await destination.GetRepositoryAsync(
                new ValueProviderContext { ExecutionContext = context.ExecutionContext }, context.CancellationToken).ConfigureAwait(false);
            var taggedDestination = $"{repository}:{prepared.PublicationTag}";
            var reference = await prepared.Runtime.CopyRemoteImageAsync(
                prepared.Reference, taggedDestination, context.CancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(reference, $"{repository}@{prepared.Digest}"))
            {
                throw new DistributedApplicationException($"Container runtime did not verify the expected content reference for destination image '{destination.Name}'.");
            }
            context.CancellationToken.ThrowIfCancellationRequested();
            EnsureConfiguration(destination.Source, configuration);
            destination.EnsureCurrentPublication();

            // Each destination owns a section so parallel fan-out cannot race on one JSON object.
            // Persist last successful evidence, but never hydrate it as proof of a new publication.
            var state = context.Services.GetRequiredService<IDeploymentStateManager>();
            var section = await state.AcquireSectionAsync($"ContainerImages:{destination.Name}", context.CancellationToken).ConfigureAwait(false);
            section.Data.Clear();
            section.Data["schemaVersion"] = 1;
            section.Data["sourceImage"] = prepared.Reference;
            section.Data["destinationImage"] = reference;
            section.Data["publicationTag"] = taggedDestination;
            await state.SaveSectionAsync(section, context.CancellationToken).ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();
            EnsureConfiguration(destination.Source, configuration);
            destination.RecordPublishedImage(repository, prepared.Digest);
            context.Summary.Add($"Image {destination.Name}", reference);
            await task.CompleteAsync(new MarkdownString($"Published **{destination.Name}** as `{reference}`"),
                CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            destination.ClearPublishedDigest();
            await task.FailAsync(new MarkdownString($"Failed to push **{destination.Name}**: {ex.Message}"),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static void EnsureConfiguration(ContainerImageResource source, ContainerImageSourceAnnotation configuration)
    {
        if (!ReferenceEquals(source.GetSource(), configuration))
        {
            throw new DistributedApplicationException(
                $"Image source '{source.Name}' changed after the publication pipeline was resolved. Retry the deployment.");
        }
    }

    private sealed record PreparedImage(IContainerRuntime Runtime, string Reference, string Digest, string PublicationTag);
}
