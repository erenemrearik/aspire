// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;

namespace Aspire.Dashboard.Model.ResourceGraph;

/// <summary>
/// Exports the visible resource graph as a Mermaid flowchart.
/// </summary>
internal static class ResourceGraphMermaidExporter
{
    public static string Export(IEnumerable<ResourceDto> resources)
    {
        var orderedResources = resources.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        var nodeIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var diagram = new StringBuilder("flowchart LR\n");

        for (var i = 0; i < orderedResources.Count; i++)
        {
            var resource = orderedResources[i];
            // Resource names can contain Mermaid syntax or reserved words such as "end".
            // Keep identifiers separate from labels, including for replicas with the same display name.
            var nodeId = "resource" + i.ToString(CultureInfo.InvariantCulture);
            nodeIds.Add(resource.Name, nodeId);
            diagram.Append("    ").Append(nodeId).Append("[\"");
            AppendLabel(diagram, resource.DisplayName);
            diagram.Append("\"]\n");
        }

        foreach (var resource in orderedResources)
        {
            foreach (var reference in resource.ReferencedNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                // Match the dashboard graph: references to filtered-out nodes aren't displayed.
                if (nodeIds.TryGetValue(reference, out var targetId))
                {
                    diagram.Append("    ").Append(nodeIds[resource.Name]).Append(" --> ").Append(targetId).Append('\n');
                }
            }
        }

        return diagram.ToString();
    }

    private static void AppendLabel(StringBuilder diagram, string label)
    {
        // Mermaid quoted labels use decimal entities (for example: api#34;v2#34;).
        // Escape HTML and entity delimiters too so labels remain text, not markup or diagram syntax.
        // https://mermaid.js.org/syntax/flowchart.html#entity-codes-to-escape-characters
        foreach (var character in label)
        {
            if (character is '"' or '#' or '&' or '<' or '>' or '\\' or '`')
            {
                diagram.Append('#').Append(((int)character).ToString(CultureInfo.InvariantCulture)).Append(';');
            }
            else
            {
                diagram.Append(char.IsControl(character) ? ' ' : character);
            }
        }
    }
}
