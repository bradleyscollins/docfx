﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.Engine
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Collections.Immutable;
    using System.Text;

    using Microsoft.DocAsCode.Build.Engine.Incrementals;
    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.Exceptions;
    using Microsoft.DocAsCode.Plugins;

    public class SingleDocumentBuilder : IDisposable
    {
        private const string PhaseName = "Build Document";
        private const string XRefMapFileName = "xrefmap.yml";

        public IEnumerable<IDocumentProcessor> Processors { get; set; }
        public IEnumerable<IInputMetadataValidator> MetadataValidators { get; set; }
        public IMarkdownServiceProvider MarkdownServiceProvider { get; set; }

        internal BuildInfo CurrentBuildInfo { get; set; }
        internal BuildInfo LastBuildInfo { get; set; }
        internal string IntermediateFolder { get; set; }
        private IMarkdownService MarkdownService { get; set; }

        public static ImmutableList<FileModel> Build(IDocumentProcessor processor, DocumentBuildParameters parameters, IMarkdownService markdownService)
        {
            var hostServiceCreator = new HostServiceCreator(null);
            var hostService = hostServiceCreator.CreateHostService(
                parameters,
                null,
                markdownService,
                null,
                processor,
                parameters.Files.EnumerateFiles());
            var phaseProcessor = new PhaseProcessor
            {
                Handlers =
                    {
                        new CompilePhaseHandler(null),
                        new LinkPhaseHandler(null, null),
                    }
            };
            phaseProcessor.Process(new List<HostService> { hostService }, parameters.MaxParallelism);
            return hostService.Models;
        }

        public Manifest Build(DocumentBuildParameters parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }
            if (parameters.OutputBaseDir == null)
            {
                throw new ArgumentException("Output folder cannot be null.", nameof(parameters) + "." + nameof(parameters.OutputBaseDir));
            }
            if (parameters.Files == null)
            {
                throw new ArgumentException("Source files cannot be null.", nameof(parameters) + "." + nameof(parameters.Files));
            }
            if (parameters.MaxParallelism <= 0)
            {
                parameters.MaxParallelism = Environment.ProcessorCount;
            }
            if (parameters.Metadata == null)
            {
                parameters.Metadata = ImmutableDictionary<string, object>.Empty;
            }

            return BuildCore(parameters);
        }

        private Manifest BuildCore(DocumentBuildParameters parameters)
        {
            using (new LoggerPhaseScope(PhaseName, false))
            using (new PerformanceScope(PhaseName, LogLevel.Verbose))
            {
                Logger.LogInfo($"Max parallelism is {parameters.MaxParallelism}.");
                Directory.CreateDirectory(parameters.OutputBaseDir);

                var context = new DocumentBuildContext(
                    Path.Combine(Directory.GetCurrentDirectory(), parameters.OutputBaseDir),
                    parameters.Files.EnumerateFiles(),
                    parameters.ExternalReferencePackages,
                    parameters.XRefMaps,
                    parameters.MaxParallelism,
                    parameters.Files.DefaultBaseDir,
                    parameters.VersionName,
                    parameters.ApplyTemplateSettings,
                    parameters.RootTocPath);

                Logger.LogVerbose("Start building document...");

                // Start building document...
                List<HostService> hostServices = null;
                IHostServiceCreator hostServiceCreator = null;
                PhaseProcessor phaseProcessor = null;
                try
                {
                    using (var templateProcessor = parameters.TemplateManager?.GetTemplateProcessor(context, parameters.MaxParallelism) ?? TemplateProcessor.DefaultProcessor)
                    {
                        using (new LoggerPhaseScope("Prepare", false))
                        using (new PerformanceScope("Prepare", LogLevel.Verbose))
                        {
                            if (MarkdownService == null)
                            {
                                using (new LoggerPhaseScope("CreateMarkdownService", true))
                                {
                                    MarkdownService = CreateMarkdownService(parameters, templateProcessor.Tokens.ToImmutableDictionary());
                                }
                            }
                            Prepare(
                                parameters,
                                context,
                                templateProcessor,
                                (MarkdownService as IHasIncrementalContext)?.GetIncrementalContextHash(),
                                out hostServiceCreator,
                                out phaseProcessor);
                        }
                        using (new LoggerPhaseScope("Load", false))
                        using (new PerformanceScope("Load", LogLevel.Verbose))
                        {
                            hostServices = GetInnerContexts(parameters, Processors, templateProcessor, hostServiceCreator).ToList();
                        }

                        BuildCore(phaseProcessor, hostServices, context);

                        return new Manifest
                        {
                            Files = context.ManifestItems.ToList(),
                            Homepages = GetHomepages(context),
                            XRefMap = ExportXRefMap(parameters, context),
                            SourceBasePath = StringExtension.ToNormalizedPath(EnvironmentContext.BaseDirectory),
                            IncrementalInfo = context.IncrementalBuildContext != null ? new List<IncrementalInfo> { context.IncrementalBuildContext.IncrementalInfo } : null,
                        };
                    }
                }
                finally
                {
                    if (hostServices != null)
                    {
                        foreach (var item in hostServices)
                        {
                            Cleanup(item);
                            item.Dispose();
                        }
                    }
                }
            }
        }

        private void BuildCore(PhaseProcessor phaseProcessor, List<HostService> hostServices, DocumentBuildContext context)
        {
            try
            {
                phaseProcessor.Process(hostServices, context.MaxParallelism);
            }
            catch (BuildCacheException e)
            {
                var message = $"Build cache was corrupted, please try force rebuild `build --force` or clear the cache files in the path: {IntermediateFolder}. Detail error: {e.Message}.";
                Logger.LogError(message);
                throw new DocfxException(message, e);
            }
        }

        private void Cleanup(HostService hostService)
        {
            hostService.Models.RunAll(m => m.Dispose());
        }

        private IEnumerable<HostService> GetInnerContexts(
            DocumentBuildParameters parameters,
            IEnumerable<IDocumentProcessor> processors,
            TemplateProcessor templateProcessor,
            IHostServiceCreator creator)
        {
            var k = from fileItem in (
                    from file in parameters.Files.EnumerateFiles()
                    from p in (from processor in processors
                               let priority = processor.GetProcessingPriority(file)
                               where priority != ProcessingPriority.NotSupported
                               group processor by priority into ps
                               orderby ps.Key descending
                               select ps.ToList()).FirstOrDefault() ?? new List<IDocumentProcessor> { null }
                    select new { file, p })
                    group fileItem by fileItem.p;

            var toHandleItems = k.Where(s => s.Key != null);
            var notToHandleItems = k.Where(s => s.Key == null);
            foreach (var item in notToHandleItems)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Cannot handle following file:");
                foreach (var f in item)
                {
                    sb.Append("\t");
                    sb.AppendLine(f.file.File);
                }
                Logger.LogWarning(sb.ToString());
            }

            foreach (var pair in (from processor in processors
                                  join item in toHandleItems on processor equals item.Key into g
                                  from item in g.DefaultIfEmpty()
                                  select new
                                  {
                                      processor,
                                      item,
                                  }).AsParallel().WithDegreeOfParallelism(parameters.MaxParallelism))
            {
                using (new LoggerPhaseScope(pair.processor.Name, true))
                {
                    var hostService = creator.CreateHostService(
                        parameters,
                        templateProcessor,
                        MarkdownService,
                        MetadataValidators,
                        pair.processor,
                        pair.item?.Select(f => f.file));
                    yield return hostService;
                }
            }
        }

        private void Prepare(
            DocumentBuildParameters parameters,
            DocumentBuildContext context,
            TemplateProcessor templateProcessor,
            string markdownServiceContextHash,
            out IHostServiceCreator hostServiceCreator,
            out PhaseProcessor phaseProcessor)
        {
            if (IntermediateFolder != null && parameters.ApplyTemplateSettings.TransformDocument)
            {
                using (new LoggerPhaseScope("CreateIncrementalBuildContext", false))
                using (new PerformanceScope("CreateIncrementalBuildContext", LogLevel.Verbose))
                {
                    context.IncrementalBuildContext = IncrementalBuildContext.Create(parameters, CurrentBuildInfo, LastBuildInfo, IntermediateFolder, markdownServiceContextHash);
                }
                hostServiceCreator = new HostServiceCreatorWithIncremental(context);
                phaseProcessor = new PhaseProcessor
                {
                    Handlers =
                                    {
                                        new CompilePhaseHandler(context).WithIncremental(),
                                        new LinkPhaseHandler(context, templateProcessor).WithIncremental(),
                                    }
                };
            }
            else
            {
                hostServiceCreator = new HostServiceCreator(context);
                phaseProcessor = new PhaseProcessor
                {
                    Handlers =
                                    {
                                        new CompilePhaseHandler(context),
                                        new LinkPhaseHandler(context, templateProcessor),
                                    }
                };
            }
        }

        private static List<HomepageInfo> GetHomepages(DocumentBuildContext context)
        {
            return context.GetTocInfo()
                .Where(s => !string.IsNullOrEmpty(s.Homepage))
                .Select(s => new HomepageInfo
                {
                    Homepage = RelativePath.GetPathWithoutWorkingFolderChar(s.Homepage),
                    TocPath = RelativePath.GetPathWithoutWorkingFolderChar(context.GetFilePath(s.TocFileKey))
                }).ToList();
        }

        /// <summary>
        /// Export xref map file.
        /// </summary>
        private static string ExportXRefMap(DocumentBuildParameters parameters, DocumentBuildContext context)
        {
            Logger.LogVerbose("Exporting xref map...");
            var xrefMap = new XRefMap();
            xrefMap.References =
                (from xref in context.XRefSpecMap.Values.AsParallel().WithDegreeOfParallelism(parameters.MaxParallelism)
                 select new XRefSpec(xref)
                 {
                     Href = context.UpdateHref(xref.Href, RelativePath.WorkingFolder)
                 }).ToList();
            xrefMap.Sort();
            string xrefMapFileNameWithVersion = string.IsNullOrEmpty(parameters.VersionName) ?
                XRefMapFileName :
                parameters.VersionName + "." + XRefMapFileName;
            YamlUtility.Serialize(
                xrefMapFileNameWithVersion,
                xrefMap,
                YamlMime.XRefMap);
            Logger.LogInfo("XRef map exported.");
            return xrefMapFileNameWithVersion;
        }

        private IMarkdownService CreateMarkdownService(DocumentBuildParameters parameters, ImmutableDictionary<string, string> tokens)
        {
            return MarkdownServiceProvider.CreateMarkdownService(
                new MarkdownServiceParameters
                {
                    BasePath = parameters.Files.DefaultBaseDir,
                    TemplateDir = parameters.TemplateDir,
                    Extensions = parameters.MarkdownEngineParameters,
                    Tokens = tokens,
                });
        }

        public void Dispose()
        {
            foreach (var processor in Processors)
            {
                Logger.LogVerbose($"Disposing processor {processor.Name} ...");
                (processor as IDisposable)?.Dispose();
            }
            (MarkdownService as IDisposable)?.Dispose();
            MarkdownService = null;
        }
    }
}

