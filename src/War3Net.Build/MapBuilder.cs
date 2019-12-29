﻿// ------------------------------------------------------------------------------
// <copyright file="MapBuilder.cs" company="Drake53">
// Licensed under the MIT license.
// See the LICENSE file in the project root for more information.
// </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using War3Net.Build.Environment;
using War3Net.Build.Info;
using War3Net.Build.Providers;
using War3Net.Build.Script;
using War3Net.Build.Widget;
using War3Net.CodeAnalysis.Jass.Renderer;
using War3Net.IO.Mpq;

namespace War3Net.Build
{
    public class MapBuilder
    {
        private string _outputMapName;
        private ushort _blockSize;
        private bool _generateListfile;

        /// <summary>
        /// Initializes a new instance of the <see cref="MapBuilder"/> class.
        /// </summary>
        public MapBuilder()
        {
            _outputMapName = "TestMap.w3x";
            _blockSize = 3;
            _generateListfile = true;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MapBuilder"/> class.
        /// </summary>
        /// <param name="mapName"></param>
        public MapBuilder(string mapName)
        {
            _outputMapName = mapName;
            _blockSize = 3;
            _generateListfile = true;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MapBuilder"/> class.
        /// </summary>
        /// <param name="mapName"></param>
        /// <param name="blockSize"></param>
        public MapBuilder(string mapName, ushort blockSize)
        {
            _outputMapName = mapName;
            _blockSize = blockSize;
            _generateListfile = true;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MapBuilder"/> class.
        /// </summary>
        /// <param name="mapName"></param>
        /// <param name="blockSize"></param>
        /// <param name="generateListfile"></param>
        public MapBuilder(string mapName, ushort blockSize, bool generateListfile)
        {
            _outputMapName = mapName;
            _blockSize = blockSize;
            _generateListfile = generateListfile;
        }

        public string OutputMapName
        {
            get => _outputMapName;
            set => _outputMapName = value;
        }

        public ushort BlockSize
        {
            get => _blockSize;
            set => _blockSize = value;
        }

        public bool GenerateListfile
        {
            get => _generateListfile;
            set => _generateListfile = value;
        }

        public bool Build(ScriptCompilerOptions compilerOptions, params string[] assetsDirectories)
        {
            if (compilerOptions is null)
            {
                throw new ArgumentNullException(nameof(compilerOptions));
            }

            Directory.CreateDirectory(compilerOptions.OutputDirectory);

            var files = new Dictionary<(string fileName, MpqLocale locale), Stream>();

            // If MapInfo is not set, search for it in assets.
            if (compilerOptions.MapInfo is null)
            {
                var mapInfoStream = FindFile(MapInfo.FileName);
                if (mapInfoStream is null)
                {
                    throw new FileNotFoundException("Could not detect required file", MapInfo.FileName);
                }

                compilerOptions.MapInfo = MapInfo.Parse(mapInfoStream);
            }

            // Generate mapInfo file
            var mapInfoPath = Path.Combine(compilerOptions.OutputDirectory, MapInfo.FileName);
            using (var mapInfoStream = File.Create(mapInfoPath))
            {
                compilerOptions.MapInfo.SerializeTo(mapInfoStream);
            }

            files.Add((MapInfo.FileName, MpqLocale.Neutral), File.OpenRead(mapInfoPath));

            // If MapEnvironment is not set, search for it in assets.
            if (compilerOptions.MapEnvironment is null)
            {
                var mapEnvironmentStream = FindFile(MapEnvironment.FileName);
                compilerOptions.MapEnvironment = mapEnvironmentStream is null
                    ? new MapEnvironment(compilerOptions.MapInfo)
                    : MapEnvironment.Parse(mapEnvironmentStream);
            }

            // Generate mapEnvironment file
            var mapEnvironmentPath = Path.Combine(compilerOptions.OutputDirectory, MapEnvironment.FileName);
            using (var mapEnvironmentStream = File.Create(mapEnvironmentPath))
            {
                compilerOptions.MapEnvironment.SerializeTo(mapEnvironmentStream);
            }

            files.Add((MapEnvironment.FileName, MpqLocale.Neutral), File.OpenRead(mapEnvironmentPath));

            // If MapUnits is not set, search for it in assets.
            if (compilerOptions.MapUnits is null)
            {
                var mapUnitsStream = FindFile(MapUnits.FileName);
                if (mapUnitsStream != null)
                {
                    compilerOptions.MapUnits = MapUnits.Parse(mapUnitsStream);
                }
            }

            // Generate mapUnits file
            if (compilerOptions.MapUnits != null)
            {
                var mapUnitsPath = Path.Combine(compilerOptions.OutputDirectory, MapUnits.FileName);
                using (var mapUnitsStream = File.Create(mapUnitsPath))
                {
                    compilerOptions.MapUnits.SerializeTo(mapUnitsStream);
                }

                files.Add((MapUnits.FileName, MpqLocale.Neutral), File.OpenRead(mapUnitsPath));
            }



            /*var references = new[] { new FolderReference(compilerOptions.SourceDirectory) };
            var references = new List<ContentReference>();
            RecursiveAddReferences(new ProjectReference(compilerOptions.SourceDirectory));

            void RecursiveAddReferences(ContentReference contentReference)
            {
                if (references.Contains(contentReference))
                {
                    return;
                }

                references.Add(contentReference);
                foreach (var reference in contentReference.GetReferences(false))
                {
                    if (reference is ProjectReference)
                    {
                        RecursiveAddReferences(reference);
                    }

                    if (reference is PackageReference packageReference)
                    {
                        // TODO: replace with better condition
                        if (packageReference.Name.StartsWith("War3Api") || packageReference.Name.StartsWith("War3Lib") || packageReference.Name == "War3Net.CodeAnalysis.Common")
                        {
                            RecursiveAddReferences(reference);
                        }
                    }
                }
            }*/

            // Generate script file
            if (compilerOptions.SourceDirectory != null)
            {
                if (Compile(compilerOptions, out var path))
                {
                    files.Add((new FileInfo(path).Name, MpqLocale.Neutral), File.OpenRead(path));
                }
                else
                {
                    return false;
                }
            }

            void EnumerateFiles(string directory)
            {
                foreach (var (fileName, locale, stream) in FileProvider.EnumerateFiles(directory))
                {
                    if (files.ContainsKey((fileName, locale)))
                    {
                        stream.Dispose();
                    }
                    else
                    {
                        files.Add((fileName, locale), stream);
                    }
                }
            }

            Stream FindFile(string searchedFile)
            {
                foreach (var assetsDirectory in assetsDirectories)
                {
                    if (string.IsNullOrWhiteSpace(assetsDirectory))
                    {
                        continue;
                    }

                    foreach (var (fileName, _, stream) in FileProvider.EnumerateFiles(assetsDirectory))
                    {
                        if (fileName == searchedFile)
                        {
                            return stream;
                        }
                    }
                }

                return null;
            }

            // Load assets
            foreach (var assetsDirectory in assetsDirectories)
            {
                if (string.IsNullOrWhiteSpace(assetsDirectory))
                {
                    continue;
                }

                EnumerateFiles(assetsDirectory);
            }

            // Load assets from projects
            /*foreach (var contentReference in references
                .Where(reference => reference is ProjectReference)
                .Select(reference => reference.Folder))
            {
                // TODO: use ProjectReference._project.Files? (since this would pre-enumerate over the files instead of returning a folder, will still need to deal with locale folders)
                // EnumerateFiles(contentReference);
            }

            // Load assets from packages
            foreach (var contentReference in references
                .Where(reference => reference is PackageReference)
                .Select(reference => Path.Combine(reference.Folder, ContentReference.ContentFolder)))
            {
                EnumerateFiles(contentReference);
            }*/

            // Generate (listfile)
            var generateListfile = compilerOptions.FileFlags.TryGetValue(ListFile.Key, out var listfileFlags)
                ? listfileFlags.HasFlag(MpqFileFlags.Exists)
                : _generateListfile; // compilerOptions.DefaultFileFlags.HasFlag(MpqFileFlags.Exists);

            if (generateListfile)
            {
                using var listFile = new ListFile(files.Select(file => file.Key.fileName));
                listFile.Finish(true);

                if (files.ContainsKey((ListFile.Key, MpqLocale.Neutral)))
                {
                    files[(ListFile.Key, MpqLocale.Neutral)].Dispose();
                    files.Remove((ListFile.Key, MpqLocale.Neutral));
                }

                files.Add((ListFile.Key, MpqLocale.Neutral), listFile.BaseStream);

                using var fileStream = File.Create(Path.Combine(compilerOptions.OutputDirectory, ListFile.Key));
                listFile.BaseStream.CopyTo(fileStream);

                /*var listfilePath = Path.Combine(compilerOptions.OutputDirectory, ListFile.Key);
                using (var listfileStream = File.Create(listfilePath))
                {
                    using (var streamWriter = new StreamWriter(listfileStream, new UTF8Encoding(false)))
                    {
                        foreach (var file in files)
                        {
                            streamWriter.WriteLine(file.Key.fileName);
                        }
                    }
                }

                if (files.ContainsKey((ListFile.Key, MpqLocale.Neutral)))
                {
                    files[(ListFile.Key, MpqLocale.Neutral)].Dispose();
                    files.Remove((ListFile.Key, MpqLocale.Neutral));
                }

                files.Add((ListFile.Key, MpqLocale.Neutral), File.OpenRead(listfilePath));*/
            }

            // Generate mpq files
            var mpqFiles = new List<MpqFile>(files.Count);
            foreach (var file in files)
            {
                var fileflags = compilerOptions.FileFlags.TryGetValue(file.Key.fileName, out var flags) ? flags : compilerOptions.DefaultFileFlags;
                if (fileflags.HasFlag(MpqFileFlags.Exists))
                {
                    // mpqFiles.Add(new MpqKnownFile(file.Key.fileName, file.Value, fileflags, file.Key.locale));
                    var mpqFile = MpqFile.New(file.Value, file.Key.fileName);
                    mpqFile.TargetFlags = fileflags;
                    mpqFile.Locale = file.Key.locale;
                    mpqFiles.Add(mpqFile);

                }
                else
                {
                    file.Value.Dispose();
                }
            }

            // Generate .mpq archive file
            var outputMap = Path.Combine(compilerOptions.OutputDirectory, _outputMapName);
            MpqArchive.Create(File.Create(outputMap), mpqFiles, blockSize: _blockSize).Dispose();

            return true;
        }

        /*public bool Compile(ScriptCompilerOptions options, out string scriptFilePath)
        {
            var compiler = ScriptCompiler.GetUnknownLanguageCompiler(options);
            if (compiler is null)
            {
                scriptFilePath = null;
                return false;
            }

            var scriptBuilder = compiler.GetScriptBuilder();
            scriptFilePath = Path.Combine(options.OutputDirectory, $"war3map{scriptBuilder.Extension}");

            var mainFunctionFile = Path.Combine(options.OutputDirectory, $"main{scriptBuilder.Extension}");
            scriptBuilder.BuildMainFunction(mainFunctionFile, options.BuilderOptions);

            var configFunctionFile = Path.Combine(options.OutputDirectory, $"config{scriptBuilder.Extension}");
            scriptBuilder.BuildConfigFunction(configFunctionFile, options.BuilderOptions);

            return compiler.Compile(mainFunctionFile, configFunctionFile);
        }*/

        public bool Compile(ScriptCompilerOptions options, out string scriptFilePath)
        {
            var compiler = GetCompiler(options);
            compiler.BuildMainAndConfig(out var mainFunctionFilePath, out var configFunctionFilePath);
            return compiler.Compile(out scriptFilePath, mainFunctionFilePath, configFunctionFilePath);
        }

        private ScriptCompiler GetCompiler(ScriptCompilerOptions options)
        {
            switch (options.MapInfo.ScriptLanguage)
            {
                case ScriptLanguage.Jass:
                    return new JassScriptCompiler(options, JassRendererOptions.Default);

                case ScriptLanguage.Lua:
                    // TODO: distinguish C# and lua
                    var rendererOptions = new CSharpLua.LuaSyntaxGenerator.SettingInfo();
                    rendererOptions.Indent = 4;

                    return new CSharpScriptCompiler(options, rendererOptions);

                default:
                    throw new Exception();
            }
        }
    }
}