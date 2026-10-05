// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIRECOMPUTE003

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Publishing;
using Aspire.Hosting.Tests.TestServices;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public class ContainerImagePublishingTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string PinnedSource = "docker.io/library/busybox@" + Digest;

    [Theory]
    [InlineData("deploy")]
    [InlineData("push")]
    [InlineData("push-first")]
    public async Task ImageOnlyPipelinePreparesOnceAndPublishesNamedDestinations(string step)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: step);
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox:latest");
        var first = builder.AddContainerRegistry("one", "first.example.com", "team").AddImage("first", source);
        var second = builder.AddContainerRegistry("two", "second.example.com").AddImage("second", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);

        Assert.Equal(["docker.io/library/busybox:latest"], runtime.RemoteResolveCalls);
        var expectedCount = step == "push-first" ? 1 : 2;
        Assert.Equal(expectedCount, runtime.RemoteCopyCalls.Count);
        Assert.All(runtime.RemoteCopyCalls, call =>
        {
            Assert.Equal(PinnedSource, call.Source);
            Assert.StartsWith("aspire-deploy-", call.Destination[(call.Destination.LastIndexOf(':') + 1)..]);
        });
        Assert.Equal("first.example.com/team/first@" + Digest, await ((IValueProvider)first.Resource).GetValueAsync(default));
        if (expectedCount == 2)
        {
            Assert.Equal("second.example.com/second@" + Digest, await ((IValueProvider)second.Resource).GetValueAsync(default));
            Assert.Single(runtime.RemoteCopyCalls.Select(call => call.Destination[(call.Destination.LastIndexOf(':') + 1)..]).Distinct());
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await ((IValueProvider)second.Resource).GetValueAsync(default));
        }
        Assert.Empty(runtime.BuildImageCalls);
        Assert.Empty(runtime.PushImageCalls);
        Assert.Empty(runtime.TagImageCalls);
    }

    [Fact]
    public async Task VerifiedReferencesArePersistedAndSummarized()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        var context = await ExecuteAsync(app);
        var state = app.Services.GetRequiredService<IDeploymentStateManager>();
        var section = await state.AcquireSectionAsync("ContainerImages:published");
        var publicationTag = section.Data["publicationTag"]!.GetValue<string>();
        Assert.True(Guid.TryParseExact(publicationTag[(publicationTag.LastIndexOf('-') + 1)..], "N", out _));

        await Verify(new
        {
            PublishedReference = await ((IValueProvider)destination.Resource).GetValueAsync(default),
            State = section.Data.ToJsonString(),
            Summary = context.Summary
        }).AddScrubber(text => text.Replace(publicationTag, "registry.example.com/published:aspire-deploy-<execution>"));
    }

    [Fact]
    public async Task EachExecutionResolvesTheSourceAgainAndFailureClearsPriorEvidence()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);
        Assert.Equal(Digest, destination.Resource.GetPublishedDigest());

        runtime.ResolveRemoteImageAsyncCallback = (_, _) => throw new DistributedApplicationException("Source is unavailable.");
        await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
        Assert.Single(runtime.RemoteCopyCalls);

        runtime.ResolveRemoteImageAsyncCallback = (_, _) => Task.FromResult(PinnedSource);
        await ExecuteAsync(app);
        Assert.Equal(3, runtime.RemoteResolveCalls.Count);
        Assert.Equal(2, runtime.RemoteCopyCalls.Count);
        Assert.Equal(Digest, destination.Resource.GetPublishedDigest());
    }

    [Theory]
    [InlineData("docker.io/library/busybox:latest")]
    [InlineData("busybox@" + Digest)]
    [InlineData("other.example.com/busybox@" + Digest)]
    public async Task IncorrectPreparedReferenceFailsBeforeCopy(string result)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        runtime.ResolveRemoteImageAsyncCallback = (_, _) => Task.FromResult(result);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
        Assert.Empty(runtime.RemoteCopyCalls);
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
    }

    [Fact]
    public async Task CopyFailureRecordsNoPublication()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        runtime.CopyRemoteImageAsyncCallback = (_, _, _) => throw new DistributedApplicationException("Registry rejected the copy.");
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
        Assert.Single(runtime.RemoteCopyCalls);
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
        var section = await app.Services.GetRequiredService<IDeploymentStateManager>().AcquireSectionAsync("ContainerImages:published");
        Assert.Empty(section.Data);
    }

    [Fact]
    public async Task ReferenceCannotUseEvidenceForAnotherRepository()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        destination.Resource.RecordPublishedImage("registry.example.com/previous-repository", Digest);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ((IValueProvider)destination.Resource).GetValueAsync(default));
        Assert.Equal("Destination image 'published' has a different registry or repository than its verified publication. Publish the image again before resolving its value.", exception.Message);
    }

    [Theory]
    [InlineData("registry.example.com/published:latest")]
    [InlineData("other.example.com/published@" + Digest)]
    [InlineData("registry.example.com/published@sha256:1123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public async Task IncorrectCopyEvidenceFailsWithoutRecordingPublication(string result)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        runtime.CopyRemoteImageAsyncCallback = (_, _, _) => Task.FromResult(result);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
        var section = await app.Services.GetRequiredService<IDeploymentStateManager>().AcquireSectionAsync("ContainerImages:published");
        Assert.Empty(section.Data);
    }

    [Fact]
    public async Task ConfigurationChangedDuringCopyCannotClaimPublicationForANewSource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        runtime.CopyRemoteImageAsyncCallback = (_, _, _) =>
        {
            source.WithImageSource("busybox:v2");
            return Task.FromResult("registry.example.com/published@" + Digest);
        };
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExcludedArtifactsOrDestinationsAreNotPublished(bool excludeSource)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        if (excludeSource)
        {
            source.ExcludeFromManifest();
        }
        else
        {
            destination.ExcludeFromManifest();
        }
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);

        Assert.Empty(runtime.RemoteResolveCalls);
        Assert.Empty(runtime.RemoteCopyCalls);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("build")]
    public async Task PublishAndBuildDoNotCopyImages(string step)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: step);
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);

        Assert.Empty(runtime.RemoteResolveCalls);
        Assert.Empty(runtime.RemoteCopyCalls);
    }

    [Fact]
    public async Task CancellationDuringCopyRecordsNoPublication()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        using var cancellation = new CancellationTokenSource();
        runtime.CopyRemoteImageAsyncCallback = (_, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The pipeline did not pass its cancellation token to the runtime.");
        };
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        var context = new PipelineContext(app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services,
            NullLogger.Instance, cancellation.Token);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(context));
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
        var section = await app.Services.GetRequiredService<IDeploymentStateManager>().AcquireSectionAsync("ContainerImages:published");
        Assert.Empty(section.Data);
    }

    [Fact]
    public async Task InferredDestinationParticipatesInDeployment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        builder.AddContainerImage("tools").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        builder.AddResource(new TestImageComputeEnvironmentResource("environment") { ImageRegistry = registry.Resource });
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);

        var destination = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DestinationImageResource>());
        Assert.Equal("tools-registry", destination.Name);
        Assert.Equal("registry.example.com/tools-registry@" + Digest, await ((IValueProvider)destination).GetValueAsync(default));
        Assert.Single(runtime.RemoteResolveCalls);
        Assert.Single(runtime.RemoteCopyCalls);
    }

    [Fact]
    public async Task MissingRegistryIsRejectedBeforeTransfer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        registry.AddImage("published", source);
        builder.Resources.Remove(registry.Resource);
        using var app = builder.Build();

        await Assert.ThrowsAsync<DistributedApplicationException>(() => app.ExecuteBeforeStartHooksAsync(default));
        Assert.Empty(runtime.RemoteResolveCalls);
        Assert.Empty(runtime.RemoteCopyCalls);
    }

    [Fact]
    public async Task RegistryProvisioningAndPushPrerequisitesCompleteBeforePreparation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        var provisioned = false;
        var loggedIn = false;
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        registry.WithPipelineStepFactory(_ => new PipelineStep
        {
            Name = "provision-registry",
            Tags = [WellKnownPipelineTags.ProvisionInfrastructure],
            Action = _ => { provisioned = true; return Task.CompletedTask; }
        });
        registry.WithPipelineStepFactory(_ => new PipelineStep
        {
            Name = "login-registry",
            DependsOnSteps = ["provision-registry"],
            RequiredBySteps = [WellKnownPipelineSteps.PushPrereq],
            Action = _ => { loggedIn = true; return Task.CompletedTask; }
        });
        registry.AddImage("published", source);
        runtime.ResolveRemoteImageAsyncCallback = (_, _) =>
        {
            Assert.True(provisioned);
            Assert.True(loggedIn);
            return Task.FromResult(PinnedSource);
        };
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);

        Assert.Single(runtime.RemoteCopyCalls);
    }

    private static FakeRemoteContainerRuntime AddRuntime(IDistributedApplicationBuilder builder)
    {
        var runtime = new FakeRemoteContainerRuntime
        {
            ResolveRemoteImageAsyncCallback = (_, _) => Task.FromResult(PinnedSource),
            // Transport references look like localhost:5000/tools:aspire-deploy-<suffix>.
            // The last colon separates the tag, not the registry's optional port.
            CopyRemoteImageAsyncCallback = (_, destination, _) => Task.FromResult(destination[..destination.LastIndexOf(':')] + "@" + Digest)
        };
        builder.Services.AddSingleton<IContainerRuntimeResolver>(runtime);
        builder.Services.AddSingleton<IDeploymentStateManager, InMemoryDeploymentStateManager>();

        return runtime;
    }

    private static async Task<PipelineContext> ExecuteAsync(DistributedApplication app)
    {
        var context = new PipelineContext(app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services,
            NullLogger.Instance, default);
        await app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(context).DefaultTimeout();

        return context;
    }
}
