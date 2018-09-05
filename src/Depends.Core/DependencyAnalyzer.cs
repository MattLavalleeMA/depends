﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Buildalyzer;
using Depends.Core.Extensions;
using Depends.Core.Graph;
using Microsoft.Extensions.Logging;
using NuGet.Commands;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Depends.Core
{
    public class DependencyAnalyzer
    {
        private ILogger _logger;
        private ILoggerFactory _loggerFactory;

        public DependencyAnalyzer(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _logger = _loggerFactory.CreateLogger(typeof(DependencyAnalyzer));
        }

        public DependencyGraph Analyze(PackageIdentity package, string framework)
        {
            var settings = Settings.LoadDefaultSettings(root: null, configFileName: null, machineWideSettings: null);
            var sourceRepositoryProvider = new SourceRepositoryProvider(settings, Repository.Provider.GetCoreV3());
            var nuGetFramework = NuGetFramework.ParseFolder(framework);
            var availablePackages = new HashSet<SourcePackageDependencyInfo>();

            using (var cacheContext = new SourceCacheContext())
            {
                ResolvePackage(package, nuGetFramework, cacheContext, NuGet.Common.NullLogger.Instance, sourceRepositoryProvider.GetRepositories(), availablePackages);


                var duplicatePackages = new HashSet<SourcePackageDependencyInfo>(availablePackages
                    .GroupBy(x => x.Id)
                    .Where(x => x.Count() > 1)
                    .SelectMany(x => x.OrderByDescending(p => p.Version, VersionComparer.Default).Skip(1)),
                    PackageIdentityComparer.Default);

                var prunedPackages = new HashSet<SourcePackageDependencyInfo>();
                PrunePackages(availablePackages.First(), availablePackages, duplicatePackages, prunedPackages);

                var rootNode = new PackageReferenceNode(package.Id, package.Version.ToString());
                var packageNodes = new Dictionary<string, PackageReferenceNode>(StringComparer.OrdinalIgnoreCase);
                var builder = new DependencyGraph.Builder(rootNode);

                foreach (var target in prunedPackages)
                {
                    var downloadResource = target.Source.GetResource<DownloadResource>();
                    var downloadResult = downloadResource.GetDownloadResourceResultAsync(new PackageIdentity(target.Id, target.Version),
                        new PackageDownloadContext(cacheContext),
                        SettingsUtility.GetGlobalPackagesFolder(settings),
                        NuGet.Common.NullLogger.Instance,
                        CancellationToken.None).Result;

                    var libItems = downloadResult.PackageReader.GetLibItems();
                    var reducer = new FrameworkReducer();
                    var nearest = reducer.GetNearest(nuGetFramework, libItems.Select(x => x.TargetFramework));

                    var assemblyReferences = libItems
                        .Where(x => x.TargetFramework.Equals(nearest))
                        .SelectMany(x => x.Items)
                        .Where(x => Path.GetExtension(x).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                        .Select(x => new AssemblyReferenceNode(Path.GetFileName(x)));

                    var frameworkItems = downloadResult.PackageReader.GetFrameworkItems();
                    nearest = reducer.GetNearest(nuGetFramework, frameworkItems.Select(x => x.TargetFramework));

                    assemblyReferences = assemblyReferences.Concat(frameworkItems
                        .Where(x => x.TargetFramework.Equals(nearest))
                        .SelectMany(x => x.Items)
                        .Select(x => new AssemblyReferenceNode(x)));

                    var packageReferenceNode = new PackageReferenceNode(target.Id, target.Version.ToString());
                    builder.WithNode(packageReferenceNode);
                    builder.WithNodes(assemblyReferences);
                    builder.WithEdges(assemblyReferences.Select(x => new Edge(packageReferenceNode, x)));
                    packageNodes.Add(target.Id, packageReferenceNode);
                }

                foreach (var target in prunedPackages)
                {
                    var packageReferenceNode = packageNodes[target.Id];
                    builder.WithEdges(target.Dependencies.Select(x =>
                        new Edge(packageReferenceNode, packageNodes[x.Id], x.VersionRange.ToString())));
                }

                return builder.Build();
            }
        }

        // TODO: Async
        private void ResolvePackage(PackageIdentity package,
            NuGetFramework framework,
            SourceCacheContext cacheContext,
            NuGet.Common.ILogger logger,
            IEnumerable<SourceRepository> repositories,
            ISet<SourcePackageDependencyInfo> availablePackages)
        {
            if (availablePackages.Contains(package))
            {
                return;
            }

            foreach (var sourceRepository in repositories)
            {
                var dependencyInfoResource = sourceRepository.GetResourceAsync<DependencyInfoResource>().Result;
                var dependencyInfo = dependencyInfoResource.ResolvePackage(
                    package, framework, cacheContext, logger, CancellationToken.None).Result;

                availablePackages.Add(dependencyInfo);

                foreach(var dependency in dependencyInfo.Dependencies)
                {
                    ResolvePackage(new PackageIdentity(dependency.Id, dependency.VersionRange.MinVersion),
                        framework, cacheContext, logger, repositories, availablePackages);
                }
            }
        }

        private static void PrunePackages(SourcePackageDependencyInfo target,
            ISet<SourcePackageDependencyInfo> availablePackages,
            ISet<SourcePackageDependencyInfo> packagesToRemove,
            ISet<SourcePackageDependencyInfo> result)
        {
            foreach (var dependency in target.Dependencies)
            {

                if (packagesToRemove.Any(x => dependency.Id.Equals(x.Id, StringComparison.OrdinalIgnoreCase) &&
                                              dependency.VersionRange.Satisfies(x.Version)))
                {
                    continue;
                }

                if (result.Any(x => dependency.Id.Equals(x.Id, StringComparison.OrdinalIgnoreCase) &&
                                              dependency.VersionRange.Satisfies(x.Version)))
                {
                    continue;
                }

                PrunePackages(availablePackages.First(x => dependency.Id.Equals(x.Id, StringComparison.OrdinalIgnoreCase) && dependency.VersionRange.Satisfies(x.Version)), availablePackages, packagesToRemove, result);
            }

            if (packagesToRemove.Contains(target) || result.Contains(target))
            {
                return;
            }

            result.Add(target);
        }

        public DependencyGraph Analyze(string projectPath, string framework = null)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException(nameof(projectPath));
            }

            if (!File.Exists(projectPath))
            {
                throw new ArgumentException("Project path does not exist.", nameof(projectPath));
            }

            var analyzerManager = new AnalyzerManager(new AnalyzerManagerOptions
            {
                LoggerFactory = _loggerFactory
            });
            var projectAnalyzer = analyzerManager.GetProject(projectPath);

            var analyzeResult = string.IsNullOrEmpty(framework) ?
                projectAnalyzer.Build() : projectAnalyzer.Build(framework);

            var projectInstance = analyzeResult.ProjectInstance;

            if (projectInstance == null)
            {
                // Todo: Something went wrong, log and return better exception.
                throw new InvalidOperationException("Unable to load project.");
            }

            if (!projectInstance.IsNetSdkProject())
            {
                // Todo: Support "legacy" projects in the future.
                throw new InvalidOperationException("Unable to load project.");
            }

            var projectAssetsFilePath = projectInstance.GetProjectAssetsFilePath();

            if (!File.Exists(projectAssetsFilePath))
            {
                // Todo: Make sure this exists in future
                throw new InvalidOperationException($"{projectAssetsFilePath} not found. Please run 'dotnet restore'");
            }

            var lockFile = new LockFileFormat().Read(projectAssetsFilePath);

            var targetFramework = analyzeResult.GetTargetFramework();

            var libraries = lockFile.Targets.Single(x => x.TargetFramework == targetFramework)
                .Libraries.Where(x => x.IsPackage()).ToList();

            var projectNode = new ProjectReferenceNode(projectPath);
            var builder = new DependencyGraph.Builder(projectNode);

            var libraryNodes = new Dictionary<string, PackageReferenceNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in libraries)
            {
                var libraryNode = library.ToNode();
                builder.WithNode(libraryNode);
                libraryNodes.Add(libraryNode.PackageId, libraryNode);

                if (library.FrameworkAssemblies.Count > 0)
                {
                    var assemblyNodes = library.FrameworkAssemblies
                        .Select(x => new AssemblyReferenceNode(x));
                    builder.WithNodes(assemblyNodes);
                    builder.WithEdges(assemblyNodes
                        .Select(x => new Edge(libraryNode, x)));
                }

                if (library.RuntimeAssemblies.Count > 0)
                {
                    var assemblyNodes = library.RuntimeAssemblies
                        .Select(x => new AssemblyReferenceNode(Path.GetFileName(x.Path)))
                        .Where(x => x.Id != "_._");

                    if (assemblyNodes.Any())
                    {
                        builder.WithNodes(assemblyNodes);
                        builder.WithEdges(assemblyNodes
                            .Select(x => new Edge(libraryNode, x)));
                    }
                }

                //if (library.CompileTimeAssemblies.Count > 0)
                //{
                //    var assemblyNodes = library.CompileTimeAssemblies
                //        .Select(x => new AssemblyReferenceNode(Path.GetFileName(x.Path)));
                //    builder.WithNodes(assemblyNodes);
                //    builder.WithEdges(assemblyNodes
                //        .Select(x => new Edge(libraryNode, x)));
                //}
            }

            foreach (var library in libraries)
            {
                var libraryNode = library.ToNode();

                if (library.Dependencies.Count > 0)
                {
                    builder.WithEdges(library.Dependencies
                        .Select(x => new Edge(libraryNode, libraryNodes[x.Id], x.VersionRange.ToString())));
                }
            }

            builder.WithEdges(projectInstance.GetItems("PackageReference")
                .Select(x => new Edge(projectNode, libraryNodes[x.EvaluatedInclude], x.GetMetadataValue("Version"))));

            var references = projectInstance.GetItems("Reference")
                .Select(x => new AssemblyReferenceNode(Path.GetFileName(x.EvaluatedInclude)));

            builder.WithNodes(references);
            builder.WithEdges(references.Select(x => new Edge(projectNode, x)));

            return builder.Build();
        }
    }
}
