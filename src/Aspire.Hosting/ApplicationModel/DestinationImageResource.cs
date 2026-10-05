// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an image artifact's publication into a specific container registry.
/// </summary>
/// <remarks>
/// References use a fully qualified repository and content digest, not a mutable tag.
/// A concrete value is available only after publication records its digest. Creating the resource
/// does not publish the image, grant pull permissions, or start a container.
/// </remarks>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport]
public sealed class DestinationImageResource : Resource, IResourceWithoutLifetime, IResourceWithParent<IResource>,
    IExpressionValue, IValueWithReferences, IResourceWithCustomWithReference<DestinationImageResource>
{
    private string? _publishedDigest;
    private ContainerImageSourceAnnotation? _publishedSource;

    /// <summary>
    /// Initializes a registry-scoped image destination.
    /// </summary>
    /// <param name="name">The destination resource name, also used as the repository name within the registry namespace.</param>
    /// <param name="source">The source image artifact.</param>
    /// <param name="registry">The parent registry resource, which must implement <see cref="IContainerRegistry"/>.</param>
    /// <exception cref="ArgumentNullException">The source or registry is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The registry does not implement <see cref="IContainerRegistry"/>.</exception>
    public DestinationImageResource(string name, ContainerImageResource source, IResource registry) : base(name)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(registry);
        if (registry is not IContainerRegistry)
        {
            throw new ArgumentException("The parent must be a container registry resource.", nameof(registry));
        }

        Source = source;
        Parent = registry;
    }

    /// <summary>
    /// Gets the source image artifact shared by this and any other publication destinations.
    /// </summary>
    public ContainerImageResource Source { get; }

    /// <summary>
    /// Gets the registry resource that owns this publication destination.
    /// </summary>
    public IResource Parent { get; }

    /// <inheritdoc/>
    public string ValueExpression
    {
        get
        {
            EnsureCurrentPublication();
            return $"{{{Name}.image}}";
        }
    }

    /// <inheritdoc/>
    [AspireExportIgnore(Reason = "Reference enumeration is app-model dependency metadata.")]
    public IEnumerable<object> References => [this, Source, Parent];

    /// <inheritdoc/>
    async ValueTask<string?> IValueProvider.GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken)
    {
        EnsureCurrentPublication();
        var registry = (IContainerRegistry)Parent;
        var endpoint = await registry.Endpoint.GetValueAsync(context, cancellationToken).ConfigureAwait(false);
        ContainerImageName.ValidateRegistry(endpoint ?? "", nameof(registry.Endpoint));
        var registryRepository = registry.Repository is not null
            ? await registry.Repository.GetValueAsync(context, cancellationToken).ConfigureAwait(false)
            : null;
        var repository = string.IsNullOrEmpty(registryRepository)
            ? Name.ToLowerInvariant()
            : $"{registryRepository}/{Name.ToLowerInvariant()}";
        ContainerImageName.ValidateRepository(repository, nameof(registry.Repository));
        var qualifiedRepository = $"{endpoint}/{repository}";
        if (qualifiedRepository.Length > 255)
        {
            throw new InvalidOperationException($"Destination image '{Name}' has a registry and repository exceeding 255 characters.");
        }

        return $"{qualifiedRepository}@{GetPublishedDigest()}";
    }

    /// <inheritdoc/>
    ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken) =>
        ((IValueProvider)this).GetValueAsync(new ValueProviderContext(), cancellationToken);

    internal void RecordPublishedDigest(string digest)
    {
        ContainerImageName.ValidateDigest(digest, nameof(digest));
        EnsureCurrentPublication();
        _publishedSource = Source.GetSource();
        _publishedDigest = digest;
    }

    internal string GetPublishedDigest()
    {
        EnsureCurrentPublication();
        if (_publishedDigest is null || !ReferenceEquals(_publishedSource, Source.GetSource()))
        {
            throw new InvalidOperationException(
                $"Destination image '{Name}' has no published digest for its current source. Publish the image before resolving its value.");
        }

        return _publishedDigest;
    }

    internal void EnsureCurrentPublication()
    {
        if (!Source.GetPublications().Any(publication => ReferenceEquals(publication.Destination, this)))
        {
            throw new InvalidOperationException($"Destination image '{Name}' is no longer an active publication of '{Source.Name}'.");
        }
    }

    internal ReferenceExpression GetImageExpression()
    {
        EnsureCurrentPublication();
        var registry = (IContainerRegistry)Parent;
        var digest = new PublishedDigestValue(this);
        if (registry.Repository is { ValueExpression.Length: > 0 })
        {
            // Empty parameter-backed namespaces remain a known Aspire manifest limitation:
            // endpoint/{repository.value}/tools@{tools.digest} can resolve to a double slash.
            return ReferenceExpression.Create($"{registry.Endpoint}/{registry.Repository}/{Name.ToLowerInvariant()}@{digest}");
        }

        return ReferenceExpression.Create($"{registry.Endpoint}/{Name.ToLowerInvariant()}@{digest}");
    }

    static IResourceBuilder<TDestination>? IResourceWithCustomWithReference<DestinationImageResource>.TryWithReference<TDestination>(
        IResourceBuilder<TDestination> builder, IResourceBuilder<IResource> source, string? connectionName, bool optional, string? name)
    {
        if (source is not IResourceBuilder<DestinationImageResource> image)
        {
            return null;
        }
        if (connectionName is not null || optional || name is not null)
        {
            throw new InvalidOperationException("Image references do not support connectionName, optional, or service discovery name options.");
        }

        return builder.WithReference(image);
    }

    private sealed class PublishedDigestValue(DestinationImageResource destination) : IExpressionValue
    {
        public string ValueExpression => $"{{{destination.Name}.digest}}";

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken) =>
            new(destination.GetPublishedDigest());
    }
}
