﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Build.Logging.StructuredLogger
{
    /// <summary>
    /// Adds the value of Private metadata if present to source items of dependencies output by RAR
    /// https://github.com/Microsoft/msbuild/blob/master/documentation/wiki/ResolveAssemblyReference.md
    /// </summary>
    public class ResolveAssemblyReferenceAnalyzer
    {
        public TimeSpan TotalRARDuration = TimeSpan.Zero;
        public HashSet<string> UsedLocations { get; } = new HashSet<string>();
        public HashSet<string> UnusedLocations { get; } = new HashSet<string>();
        private readonly HashSet<string> currentUsedLocations = new HashSet<string>();

        public void AnalyzeResolveAssemblyReference(Task rar)
        {
            currentUsedLocations.Clear();

            var results = rar.FindChild<Folder>(c => c.Name == "Results");
            var parameters = rar.FindChild<Folder>(c => c.Name == "Parameters");

            TotalRARDuration += rar.Duration;

            IList<string> searchPaths = null;
            if (parameters != null)
            {
                var searchPathsNode = parameters.FindChild<NamedNode>(c => c.Name == "SearchPaths");
                if (searchPathsNode != null)
                {
                    searchPaths = searchPathsNode.Children.Select(c => c.ToString()).ToArray();
                }
            }

            if (results != null)
            {
                results.SortChildren();

                foreach (var reference in results.Children.OfType<Parameter>())
                {
                    const string ResolvedFilePathIs = "Resolved file path is \"";
                    string resolvedFilePath = null;
                    var resolvedFilePathNode = reference.FindChild<Item>(i => i.ToString().StartsWith(ResolvedFilePathIs));
                    if (resolvedFilePathNode != null)
                    {
                        var text = resolvedFilePathNode.ToString();
                        resolvedFilePath = text.Substring(ResolvedFilePathIs.Length, text.Length - ResolvedFilePathIs.Length - 2);
                    }

                    const string ReferenceFoundAt = "Reference found at search path location \"";
                    var foundAtLocation = reference.FindChild<Item>(i => i.ToString().StartsWith(ReferenceFoundAt));
                    if (foundAtLocation != null)
                    {
                        var text = foundAtLocation.ToString();
                        var location = text.Substring(ReferenceFoundAt.Length, text.Length - ReferenceFoundAt.Length - 2);

                        // filter out the case where the assembly is resolved from the AssemblyFiles parameter
                        // In this case the location matches the resolved file path.
                        if (resolvedFilePath == null || resolvedFilePath != location)
                        {
                            UsedLocations.Add(location);
                            currentUsedLocations.Add(location);
                        }
                    }

                    if (reference.Name.StartsWith("Dependency ") || reference.Name.StartsWith("Unified Dependency "))
                    {
                        bool foundNotCopyLocalBecauseMetadata = false;
                        var requiredBy = new List<Item>();
                        foreach (var message in reference.Children.OfType<Item>())
                        {
                            string text = message.Text;
                            if (text.StartsWith("Required by \""))
                            {
                                requiredBy.Add(message);
                            }
                            else if (text == @"This reference is not ""CopyLocal"" because at least one source item had ""Private"" set to ""false"" and no source items had ""Private"" set to ""true"".")
                            {
                                foundNotCopyLocalBecauseMetadata = true;
                            }
                        }

                        if (foundNotCopyLocalBecauseMetadata)
                        {
                            var assemblies = rar.FindChild<Folder>("Parameters")?.FindChild<Parameter>("Assemblies");
                            if (assemblies != null)
                            {
                                var dictionary = assemblies.Children
                                    .OfType<Item>()
                                    .GroupBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
                                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                                foreach (var sourceItem in requiredBy)
                                {
                                    int prefixLength = "Required by \"".Length;
                                    string text = sourceItem.Text;
                                    var referenceName = text.Substring(prefixLength, text.Length - prefixLength - 2);
                                    Item foundSourceItem;
                                    if (dictionary.TryGetValue(referenceName, out foundSourceItem))
                                    {
                                        foreach (var metadata in foundSourceItem.Children.OfType<Metadata>())
                                        {
                                            if (metadata.Name == "Private")
                                            {
                                                sourceItem.AddChild(new Metadata() { Name = metadata.Name, Value = metadata.Value });
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (searchPaths != null)
            {
                foreach (var searchPath in searchPaths)
                {
                    if (currentUsedLocations.Contains(searchPath))
                    {
                        var usedLocations = rar.GetOrCreateNodeWithName<Folder>("Used locations");
                        usedLocations.AddChild(new Item { Text = searchPath });
                        UnusedLocations.Remove(searchPath);
                    }
                    else
                    {
                        var unusedLocations = rar.GetOrCreateNodeWithName<Folder>("Unused locations");
                        unusedLocations.AddChild(new Item { Text = searchPath });
                        if (!UsedLocations.Contains(searchPath))
                        {
                            UnusedLocations.Add(searchPath);
                        }
                        else
                        {
                            UnusedLocations.Remove(searchPath);
                        }
                    }
                }
            }
        }

        public void AppendFinalReport(Build build)
        {
            if (UsedLocations.Any())
            {
                var usedLocationsNode = build.GetOrCreateNodeWithName<Folder>("Used AssemblySearchPaths locations");
                foreach (var location in UsedLocations.OrderBy(s => s))
                {
                    usedLocationsNode.AddChild(new Item { Text = location });
                }
            }

            if (UnusedLocations.Any())
            {
                var unusedLocationsNode = build.GetOrCreateNodeWithName<Folder>("Unused AssemblySearchPaths locations");
                foreach (var location in UnusedLocations.OrderBy(s => s))
                {
                    unusedLocationsNode.AddChild(new Item { Text = location });
                }
            }
        }
    }
}