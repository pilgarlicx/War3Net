﻿// ------------------------------------------------------------------------------
// <copyright file="MapBuffObjectData.cs" company="Drake53">
// Licensed under the MIT license.
// See the LICENSE file in the project root for more information.
// </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace War3Net.Build.Object
{
    public sealed class MapBuffObjectData
    {
        public const string FileName = "war3map.w3h";
        public const ObjectDataFormatVersion LatestVersion = ObjectDataFormatVersion.Normal;

        private readonly Dictionary<int, ObjectModification> _baseModifications;
        private readonly Dictionary<int, ObjectModification> _newModifications;

        private ObjectDataFormatVersion _fileFormatVersion;

        public MapBuffObjectData(params ObjectModification[] modifications)
            : this()
        {
            SetData(modifications);

            _fileFormatVersion = LatestVersion;
        }

        internal MapBuffObjectData()
        {
            _baseModifications = new Dictionary<int, ObjectModification>();
            _newModifications = new Dictionary<int, ObjectModification>();
        }

        public static MapBuffObjectData Default => new MapBuffObjectData() { _fileFormatVersion = ObjectDataFormatVersion.Normal, };

        public static bool IsRequired => false;

        public ObjectDataFormatVersion FormatVersion
        {
            get => _fileFormatVersion;
            set => _fileFormatVersion = value;
        }

        public int BaseModificationCount => _baseModifications.Count;

        public int NewModificationCount => _newModifications.Count;

        public static MapBuffObjectData Parse(Stream stream, bool leaveOpen = false)
        {
            try
            {
                var data = new MapBuffObjectData();
                using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true), leaveOpen))
                {
                    data._fileFormatVersion = (ObjectDataFormatVersion)reader.ReadInt32();
                    if (!Enum.IsDefined(typeof(ObjectDataFormatVersion), data._fileFormatVersion))
                    {
                        throw new NotSupportedException($"Unknown version of '{FileName}': {data._fileFormatVersion}");
                    }

                    var baseModificationCount = reader.ReadInt32();
                    for (var i = 0; i < baseModificationCount; i++)
                    {
                        var mod = ObjectModification.Parse(stream, false, true);
                        data._baseModifications.Add(mod.OldId, mod);
                    }

                    var newModificationCount = reader.ReadInt32();
                    for (var i = 0; i < newModificationCount; i++)
                    {
                        var mod = ObjectModification.Parse(stream, false, true);
                        data._newModifications.Add(mod.NewId, mod);
                    }
                }

                return data;
            }
            catch (DecoderFallbackException e)
            {
                throw new InvalidDataException($"The '{FileName}' file contains invalid characters.", e);
            }
            catch (EndOfStreamException e)
            {
                throw new InvalidDataException($"The '{FileName}' file is missing data, or its data is invalid.", e);
            }
            catch
            {
                throw;
            }
        }

        public static void Serialize(MapBuffObjectData objectData, Stream stream, bool leaveOpen = false)
        {
            objectData.SerializeTo(stream, leaveOpen);
        }

        public void SerializeTo(Stream stream, bool leaveOpen = false)
        {
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen))
            {
                writer.Write((int)_fileFormatVersion);

                writer.Write(_baseModifications.Count);
                foreach (var data in _baseModifications)
                {
                    data.Value.WriteTo(writer);
                }

                writer.Write(_newModifications.Count);
                foreach (var data in _newModifications)
                {
                    data.Value.WriteTo(writer);
                }
            }
        }

        public IEnumerable<ObjectModification> GetBaseData()
        {
            return _baseModifications.Values;
        }

        public ObjectModification GetBaseData(int id)
        {
            return _baseModifications[id];
        }

        public void SetBaseData(params ObjectModification[] data)
        {
            _baseModifications.Clear();
            foreach (var mod in data)
            {
                _baseModifications.Add(mod.OldId, mod);
            }
        }

        public IEnumerable<ObjectModification> GetNewData()
        {
            return _newModifications.Values;
        }

        public ObjectModification GetNewData(int id)
        {
            return _newModifications[id];
        }

        public void SetNewData(params ObjectModification[] data)
        {
            _newModifications.Clear();
            foreach (var mod in data)
            {
                _newModifications.Add(mod.NewId, mod);
            }
        }

        public IEnumerable<ObjectModification> GetData()
        {
            return GetBaseData().Concat(GetNewData());
        }

        public ObjectModification GetData(int id)
        {
            return _baseModifications.TryGetValue(id, out var baseMod) ? baseMod : _newModifications[id];
        }

        public void SetData(params ObjectModification[] data)
        {
            _baseModifications.Clear();
            _newModifications.Clear();
            foreach (var mod in data)
            {
                if (mod.NewId == 0)
                {
                    _baseModifications.Add(mod.OldId, mod);
                }
                else
                {
                    _newModifications.Add(mod.NewId, mod);
                }
            }
        }
    }
}