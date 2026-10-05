// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;

#pragma warning disable ASPIREPIPELINES003

namespace Aspire.Hosting;

/// <summary>
/// Provides methods for modeling source image artifacts and registry-scoped image destinations.
/// </summary>
public static class ContainerImageResourceBuilderExtensions
{
    /// <summary>
    /// Adds a standalone image artifact whose source is configured separately.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The source artifact resource name.</param>
    /// <returns>The image artifact builder.</returns>
    /// <remarks>
    /// Configure an existing registry image with <c>WithImageSource</c>. Sources are independent
    /// of destinations and can be shared across registries. This experimental API registers
    /// artifacts in publish mode only; local image preparation and registry emulation are not implemented.
    /// Deployment resolves each associated remote source once and publishes its complete referenced
    /// content to each destination using Docker with Buildx. Separate OCI referrers are not copied.
    /// </remarks>
    /// <example>
    /// <code>
    /// var source = builder.AddContainerImage("tools").WithImageSource("ghcr.io/example/tools:v1");
    /// var destination = registry.AddImage("published-tools", source);
    /// app.WithEnvironment("IMAGE_NAME", destination);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">The builder or name is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is empty or invalid.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport]
    public static IResourceBuilder<ContainerImageResource> AddContainerImage(
        this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        var resource = new ContainerImageResource(name);
        var resourceBuilder = builder.ExecutionContext.IsRunMode
            ? builder.CreateResourceBuilder(resource)
            : builder.AddResource(resource);
        ContainerImagePublishing.Configure(builder, resource);

        return resourceBuilder.WithManifestPublishingCallback(context => WriteSourceManifestAsync(context, resource));
    }

    /// <summary>
    /// Configures an existing registry image as the source of an image artifact.
    /// </summary>
    /// <param name="builder">The source artifact builder.</param>
    /// <param name="image">The image reference, including an optional tag, digest, or both.</param>
    /// <returns>The original source artifact builder.</returns>
    /// <remarks>
    /// Source configuration is last-wins. This method records deferred preparation intent;
    /// it does not pull or push an image. A source tag must be pinned to content during preparation,
    /// before publication to any destinations. Reconfiguration invalidates previously recorded publication digests.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The builder or image is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source image reference is invalid.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport("withContainerImageSource", MethodName = "withImageSource")]
    public static IResourceBuilder<ContainerImageResource> WithImageSource(
        this IResourceBuilder<ContainerImageResource> builder, string image)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var source = ContainerImageName.ParseSource(image);

        return builder.WithAnnotation(new ContainerImageSourceAnnotation(source), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Adds a named publication destination for an image artifact in a container registry.
    /// </summary>
    /// <typeparam name="TRegistry">The registry resource type.</typeparam>
    /// <param name="builder">The registry builder.</param>
    /// <param name="name">The destination resource name, also used as the repository name within the registry namespace.</param>
    /// <param name="image">The source image artifact builder.</param>
    /// <returns>A builder for the registry-scoped destination image.</returns>
    /// <remarks>
    /// Each call creates a distinct resource. Destinations are additive and can share a source,
    /// including multiple repositories within one registry. References use the published content digest,
    /// never a mutable tag. No publication or permission grant is performed during model construction.
    /// Deployment publishes this destination after source preparation and registry prerequisites.
    /// Default publication tags use the same <c>aspire-deploy-yyyyMMddHHmmss</c> UTC label as compute images.
    /// Tags are retained transport addresses; consumer references use verified digests.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is invalid or the builders belong to different applications.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport("addRegistryImage", MethodName = "addImage")]
    public static IResourceBuilder<DestinationImageResource> AddImage<TRegistry>(
        this IResourceBuilder<TRegistry> builder, [ResourceName] string name, IResourceBuilder<ContainerImageResource> image)
        where TRegistry : IResource, IContainerRegistry
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ValidateApplication(builder.ApplicationBuilder, image.ApplicationBuilder);
        var resource = new DestinationImageResource(name, image.Resource, builder.Resource);
        var destination = builder.ApplicationBuilder.ExecutionContext.IsRunMode
            ? builder.ApplicationBuilder.CreateResourceBuilder(resource)
            : builder.ApplicationBuilder.AddResource(resource);
        foreach (var inferred in image.Resource.Annotations.OfType<ContainerImagePublicationAnnotation>()
            .Where(publication => publication.DefaultEnvironment is not null).ToArray())
        {
            image.Resource.Annotations.Remove(inferred);
            builder.ApplicationBuilder.Resources.Remove(inferred.Destination);
        }
        ConfigureDestination(resource);
        image.WithAnnotation(new ContainerImagePublicationAnnotation(resource, defaultEnvironment: null));

        return destination;
    }

    /// <summary>
    /// Injects a digest-qualified destination image reference into an environment variable.
    /// </summary>
    /// <typeparam name="T">The consuming resource type.</typeparam>
    /// <param name="builder">The consumer builder.</param>
    /// <param name="name">The environment variable name.</param>
    /// <param name="image">The registry-scoped destination image.</param>
    /// <returns>The original consumer builder.</returns>
    /// <remarks>
    /// Preserves destination, source, and registry provenance without resolving values during construction.
    /// Deployment waits for the image publication. Azure Container Registry image references
    /// grant the consumer's managed identity pull access. This does not start a container.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The builders belong to different applications.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the canonical withEnvironment dispatcher.")]
    public static IResourceBuilder<T> WithEnvironment<T>(
        this IResourceBuilder<T> builder, string name, IResourceBuilder<DestinationImageResource> image)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(image);
        ValidateApplication(builder.ApplicationBuilder, image.ApplicationBuilder);

        return builder.WithEnvironment(name, (IExpressionValue)image.Resource);
    }

    /// <summary>
    /// Declares that a resource consumes a registry-scoped destination image.
    /// </summary>
    /// <typeparam name="T">The consuming resource type.</typeparam>
    /// <param name="builder">The consumer builder.</param>
    /// <param name="image">The destination image builder.</param>
    /// <returns>The original consumer builder.</returns>
    /// <remarks>
    /// Records image consumption for publication ordering and provider-specific pull access.
    /// Deployment waits for verified image publication. Azure Container Registry grants the
    /// consumer's managed identity pull access. Other registry providers must configure pull access separately.
    /// This does not inject an image environment variable or implement local dispatch.
    /// Use <c>WithEnvironment</c> separately to choose an environment variable name.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required builder is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The builders belong to different applications.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Polyglot AppHosts use custom resource dispatch through the canonical withReference export.")]
    public static IResourceBuilder<T> WithReference<T>(
        this IResourceBuilder<T> builder, IResourceBuilder<DestinationImageResource> image)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(image);
        ValidateApplication(builder.ApplicationBuilder, image.ApplicationBuilder);
        if (!builder.Resource.Annotations.OfType<DestinationImageReferenceAnnotation>()
            .Any(reference => ReferenceEquals(reference.Image, image.Resource)))
        {
            builder.WithAnnotation(new DestinationImageReferenceAnnotation(image.Resource));
            builder.WithRelationship(image.Resource, KnownRelationshipTypes.Reference);
        }

        return builder;
    }

    private static void ValidateApplication(IDistributedApplicationBuilder first, IDistributedApplicationBuilder second)
    {
        if (!ReferenceEquals(first, second))
        {
            throw new ArgumentException("The image, registry, and consumer must belong to the same distributed application.");
        }
    }

    internal static void ConfigureDestination(DestinationImageResource resource) =>
        resource.Annotations.Add(new ManifestPublishingCallbackAnnotation(context => WriteDestinationManifestAsync(context, resource)));

    private static Task WriteSourceManifestAsync(ManifestPublishingContext context, ContainerImageResource resource)
    {
        context.Writer.WriteString("type", "containerimage.v0");
        context.Writer.WriteString("source", resource.GetSource().Image);
        context.Writer.WriteStartObject("publications");
        foreach (var publication in resource.GetPublications().OrderBy(publication => publication.Destination.Name, StringComparers.ResourceName))
        {
            context.Writer.WriteStartObject(publication.Destination.Name);
            context.Writer.WriteString("registry", publication.Registry.Name);
            context.Writer.WriteString("image", publication.Destination.ValueExpression);
            context.TryAddDependentResources(publication.Destination);
            context.Writer.WriteEndObject();
        }
        context.Writer.WriteEndObject();

        return Task.CompletedTask;
    }

    private static Task WriteDestinationManifestAsync(ManifestPublishingContext context, DestinationImageResource resource)
    {
        context.Writer.WriteString("type", "containerimagepublication.v0");
        context.Writer.WriteString("source", resource.Source.Name);
        context.Writer.WriteString("registry", resource.Parent.Name);
        context.Writer.WriteString("image", resource.GetImageExpression().ValueExpression);
        // The digest is a publication output, not a value inferred from a mutable source tag.
        context.TryAddDependentResources(resource.Source);
        context.TryAddDependentResources(resource.Parent);

        return Task.CompletedTask;
    }
}
