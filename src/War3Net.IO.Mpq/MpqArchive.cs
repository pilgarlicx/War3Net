// ------------------------------------------------------------------------------
// <copyright file="MpqArchive.cs" company="Foole (fooleau@gmail.com)">
// Copyright (c) 2006 Foole (fooleau@gmail.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>
// ------------------------------------------------------------------------------

#pragma warning disable CA1710 // Identifiers should have correct suffix

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

#if NETCOREAPP3_0
using System.Diagnostics.CodeAnalysis;
#endif

namespace War3Net.IO.Mpq
{
    /// <summary>
    /// Represents a MoPaQ file, that is used to archive files.
    /// </summary>
    public sealed class MpqArchive : IDisposable, IEnumerable<MpqEntry>
    {
        /// <summary>
        /// Default value for an <see cref="MpqArchive"/>'s blocksize.
        /// </summary>
        public const ushort DefaultBlockSize = 3;

        // The MPQ header will always start at an offset aligned to 512 bytes.
        private const int PreArchiveAlignBytes = 0x200;
        private const int BlockSizeModifier = 0x200;

        private readonly Stream _baseStream;
        private readonly long _headerOffset;
        private readonly int _blockSize;
        private readonly bool _archiveBeforeTables;

        private readonly MpqHeader _mpqHeader;
        private readonly HashTable _hashTable;
        private readonly BlockTable _blockTable;

        /// <summary>
        /// Initializes a new instance of the <see cref="MpqArchive"/> class.
        /// </summary>
        /// <param name="sourceStream">The <see cref="Stream"/> from which to load an <see cref="MpqArchive"/>.</param>
        /// <param name="loadListfile">If true, automatically execute <see cref="AddListfileFilenames()"/> after the <see cref="MpqArchive"/> is initialized.</param>
        /// <exception cref="ArgumentNullException">Thrown when the <paramref name="sourceStream"/> is null.</exception>
        /// <exception cref="MpqParserException">Thrown when the <see cref="MpqHeader"/> could not be found, or when the MPQ format version is not 0.</exception>
        public MpqArchive(Stream sourceStream, bool loadListfile = false)
        {
            _baseStream = sourceStream ?? throw new ArgumentNullException(nameof(sourceStream));

            if (!TryLocateMpqHeader(_baseStream, out var mpqHeader, out _headerOffset))
            {
                throw new MpqParserException("Unable to locate MPQ header.");
            }

            _mpqHeader = mpqHeader;
            _blockSize = BlockSizeModifier << _mpqHeader.BlockSize;
            _archiveBeforeTables = IsArchiveBeforeTables();

            if (_mpqHeader.HashTableOffsetHigh != 0 || _mpqHeader.ExtendedBlockTableOffset != 0 || _mpqHeader.BlockTableOffsetHigh != 0)
            {
                throw new MpqParserException("MPQ format version 1 features are not supported");
            }

            using (var reader = new BinaryReader(_baseStream, new UTF8Encoding(), true))
            {
                // Load hash table
                _baseStream.Seek(_mpqHeader.HashTablePos, SeekOrigin.Begin);
                _hashTable = new HashTable(reader, _mpqHeader.HashTableSize);

                // Load entry table
                _baseStream.Seek(_mpqHeader.BlockTablePos, SeekOrigin.Begin);
                var size = _archiveBeforeTables && _mpqHeader.BlockTableSize > MpqTable.MaxSize
                    ? (uint)(_baseStream.Length - _baseStream.Position) / MpqEntry.Size
                    : _mpqHeader.BlockTableSize;
                _blockTable = new BlockTable(reader, size, (uint)_headerOffset);
            }

            if (loadListfile)
            {
                AddListfileFilenames();
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MpqArchive"/> class.
        /// </summary>
        /// <param name="sourceStream">The <see cref="Stream"/> containing pre-archive data. Can be null.</param>
        /// <param name="mpqFiles">The <see cref="MpqFile"/>s that should be added to the archive.</param>
        /// <param name="hashTableSize">The desired size of the <see cref="BlockTable"/>. Larger size decreases the likelihood of hash collisions.</param>
        /// <param name="blockSize">The size of blocks in compressed files, which is used to enable seeking.</param>
        /// <param name="writeArchiveFirst">If true, the archive files will be positioned directly after the header. Otherwise, the hashtable and blocktable will come first.</param>
        /// <exception cref="ArgumentNullException">Thrown when the <paramref name="mpqFiles"/> collection is null.</exception>
        public MpqArchive(Stream? sourceStream, ICollection<MpqFile> mpqFiles, ushort? hashTableSize = null, ushort blockSize = DefaultBlockSize, bool writeArchiveFirst = true)
        {
            _baseStream = AlignStream(sourceStream);

            _headerOffset = _baseStream.Position;
            _blockSize = BlockSizeModifier << blockSize;
            _archiveBeforeTables = writeArchiveFirst;

            var fileCount = (uint)(mpqFiles ?? throw new ArgumentNullException(nameof(mpqFiles))).Count;

            _hashTable = new HashTable(Math.Max(hashTableSize ?? fileCount * 8, fileCount));
            _blockTable = new BlockTable(fileCount);

            using (var writer = new BinaryWriter(_baseStream, new UTF8Encoding(false, true), true))
            {
                // Skip the MPQ header, since its contents will be calculated afterwards.
                writer.Seek((int)MpqHeader.Size, SeekOrigin.Current);

                uint hashTableEntries = 0;

                // Write Archive
                var fileIndex = 0U;
                var fileOffset = _archiveBeforeTables ? MpqHeader.Size : throw new NotImplementedException();
                var filePos = fileOffset;

                foreach (var mpqFile in mpqFiles)
                {
                    mpqFile.AddToArchive((uint)_headerOffset, fileIndex, filePos, _hashTable.Mask);

                    if (_archiveBeforeTables)
                    {
                        mpqFile.SerializeTo(writer, true);
                    }

                    hashTableEntries += _hashTable.Add(mpqFile.MpqHash, mpqFile.HashIndex, mpqFile.HashCollisions);
                    _blockTable.Add(mpqFile.MpqEntry);

                    filePos += mpqFile.MpqEntry.CompressedSize;
                    fileIndex++;
                }

                // Match size of blocktable with amount of occupied entries in hashtable
                /*
                for ( var i = blockTable.Size; i < hashTableEntries; i++ )
                {
                    var entry = MpqEntry.Dummy;
                    entry.SetPos( filePos );
                    blockTable.Add( entry );
                }
                blockTable.UpdateSize();
                */

                _hashTable.SerializeTo(writer);
                _blockTable.SerializeTo(writer);

                if (!_archiveBeforeTables)
                {
                    foreach (var mpqFile in mpqFiles)
                    {
                        mpqFile.SerializeTo(writer, true);
                    }
                }

                writer.Seek((int)_headerOffset, SeekOrigin.Begin);

                _mpqHeader = new MpqHeader(filePos - fileOffset, _hashTable.Size, _blockTable.Size, blockSize, _archiveBeforeTables);
                _mpqHeader.WriteToStream(writer);
            }
        }

        /// <summary>
        /// Gets the <see cref="MpqHeader"/> of this <see cref="MpqArchive"/>.
        /// </summary>
        public MpqHeader Header => _mpqHeader;

        /// <summary>
        /// Gets the size of the <see cref="BlockTable"/>.
        /// </summary>
        public int Count => (int)_blockTable.Size;

        /// <summary>
        /// Gets the stream that represents this <see cref="MpqArchive"/>.
        /// </summary>
        internal Stream BaseStream => _baseStream;

        /// <summary>
        /// Gets the length (in bytes) of blocks in compressed files.
        /// </summary>
        internal int BlockSize => _blockSize;

        // internal uint HashTableSize => _hashTable.Size;

        // internal long HeaderOffset => _headerOffset;

        /// <summary>
        /// Retrieves the <see cref="MpqEntry"/> at the given <paramref name="index"/> of the archive's <see cref="BlockTable"/>.
        /// </summary>
        /// <remarks>
        /// Use the <see cref="FileExists(string, out int)"/> method to get the index of a certain <see cref="MpqEntry"/>.
        /// </remarks>
        /// <param name="index">The <paramref name="index"/> of the <see cref="MpqEntry"/> in the <see cref="BlockTable"/>.</param>
        /// <returns>The <see cref="MpqEntry"/> at the given <paramref name="index"/> of the <see cref="BlockTable"/>.</returns>
        public MpqEntry this[int index] => _blockTable[index];

        /// <summary>
        /// Opens an existing <see cref="MpqArchive"/> for reading.
        /// </summary>
        /// <param name="path">The <see cref="MpqArchive"/> to open.</param>
        /// <param name="loadListfile">If true, automatically execute <see cref="AddListfileFilenames()"/> after the <see cref="MpqArchive"/> is initialized.</param>
        /// <returns>An <see cref="MpqArchive"/> opened from the specified <paramref name="path"/>.</returns>
        /// <exception cref="IOException">Thrown when unable to create a <see cref="FileStream"/> from the given <paramref name="path"/>.</exception>
        /// <exception cref="MpqParserException">Thrown when the <see cref="MpqHeader"/> could not be found, or when the MPQ format version is not 0.</exception>
        public static MpqArchive Open(string path, bool loadListfile = false)
        {
            FileStream fileStream;

            try
            {
                fileStream = File.OpenRead(path);
            }
            catch (Exception exception)
            {
                throw new IOException($"Failed to open the {nameof(MpqArchive)}", exception);
            }

            return Open(fileStream, loadListfile);
        }

        /// <summary>
        /// Opens an existing <see cref="MpqArchive"/> for reading.
        /// </summary>
        /// <param name="sourceStream">The <see cref="Stream"/> from which to load an <see cref="MpqArchive"/>.</param>
        /// <param name="loadListfile">If true, automatically execute <see cref="AddListfileFilenames()"/> after the <see cref="MpqArchive"/> is initialized.</param>
        /// <returns>An <see cref="MpqArchive"/> opened from the specified <paramref name="sourceStream"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the <paramref name="sourceStream"/> is null.</exception>
        /// <exception cref="MpqParserException">Thrown when the <see cref="MpqHeader"/> could not be found, or when the MPQ format version is not 0.</exception>
        public static MpqArchive Open(Stream sourceStream, bool loadListfile = false)
        {
            return new MpqArchive(sourceStream, loadListfile);
        }

        /// <summary>
        /// Creates a new <see cref="MpqArchive"/>.
        /// </summary>
        /// <param name="path">The path and name of the <see cref="MpqArchive"/> to create.</param>
        /// <param name="mpqFiles">The <see cref="MpqFile"/>s that should be added to the archive.</param>
        /// <param name="hashTableSize">The desired size of the <see cref="BlockTable"/>. Larger size decreases the likelihood of hash collisions.</param>
        /// <param name="blockSize">The size of blocks in compressed files, which is used to enable seeking.</param>
        /// <returns>An <see cref="MpqArchive"/> created as a new file at the specified <paramref name="path"/>.</returns>
        /// <exception cref="IOException">Thrown when unable to create a <see cref="FileStream"/> from the given <paramref name="path"/>.</exception>
        public static MpqArchive Create(string path, ICollection<MpqFile> mpqFiles, ushort? hashTableSize = null, ushort blockSize = DefaultBlockSize)
        {
            FileStream fileStream;

            try
            {
                fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
            }
            catch (Exception exception)
            {
                throw new IOException($"Failed to create a {nameof(FileStream)}", exception);
            }

            return Create(fileStream, mpqFiles, hashTableSize, blockSize);
        }

        /// <summary>
        /// Creates a new <see cref="MpqArchive"/>.
        /// </summary>
        /// <param name="sourceStream">The <see cref="Stream"/> containing pre-archive data. Can be null.</param>
        /// <param name="mpqFiles">The <see cref="MpqFile"/>s that should be added to the archive.</param>
        /// <param name="hashTableSize">The desired size of the <see cref="BlockTable"/>. Larger size decreases the likelihood of hash collisions.</param>
        /// <param name="blockSize">The size of blocks in compressed files, which is used to enable seeking.</param>
        /// <returns>An <see cref="MpqArchive"/> that is created.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the <paramref name="mpqFiles"/> collection is null.</exception>
        public static MpqArchive Create(Stream sourceStream, ICollection<MpqFile> mpqFiles, ushort? hashTableSize = null, ushort blockSize = DefaultBlockSize)
        {
            return new MpqArchive(sourceStream, mpqFiles, hashTableSize, blockSize);
        }

        /// <summary>
        /// Recreate an <see cref="MpqArchive"/> with a single file in its archive replaced.
        /// </summary>
        /// <param name="inputArchivePath">Path to the input Mpq Archive file.</param>
        /// <param name="outputArchivePath">Path to the output Mpq Archive file.</param>
        /// <param name="filename">The internal filename of the file that will be replaced.</param>
        /// <param name="fileStream">The content which will replace the original file content.</param>
        public static void ReplaceFile(string inputArchivePath, string outputArchivePath, string filename, Stream fileStream)
        {
            using (var archive = Open(inputArchivePath))
            {
                if (!archive.TryGetHashEntry(filename, out var hash))
                {
                    throw new FileNotFoundException($"File not found: {filename}");
                }

                var entry = archive[(int)hash.BlockIndex];
                var fileLength = fileStream?.Length ?? 0;
                var sizeDifference = fileLength - entry.CompressedSize;

                Directory.CreateDirectory(new FileInfo(outputArchivePath).DirectoryName);
                var archiveStream = archive._baseStream;
                archiveStream.Position = 0;

                using (var outputStream = File.Create(outputArchivePath))
                {
                    using (var binaryWriter = new BinaryWriter(outputStream, new UTF8Encoding(false, true)))
                    {
                        using (var binaryReader = new BinaryReader(archiveStream, new UTF8Encoding()))
                        {
                            var headerOffset = archive._headerOffset;
                            binaryWriter.Write(binaryReader.ReadBytes((int)headerOffset));
                            binaryReader.ReadBytes((int)MpqHeader.Size);

                            var header = archive._mpqHeader;
                            var archiveBeforeTables = header.DataOffset == 32 || header.BlockTablePos != 32;

                            binaryWriter.Write(header.ID);
                            binaryWriter.Write(header.DataOffset);
                            binaryWriter.Write((uint)(header.ArchiveSize + sizeDifference));
                            binaryWriter.Write(header.MpqVersion);
                            binaryWriter.Write(header.BlockSize);
                            binaryWriter.Write((uint)((header.HashTablePos - headerOffset) + (archiveBeforeTables ? sizeDifference : 0)));
                            binaryWriter.Write((uint)((header.BlockTablePos - headerOffset) + (archiveBeforeTables ? sizeDifference : 0)));
                            binaryWriter.Write(header.HashTableSize);
                            binaryWriter.Write(header.BlockTableSize);

                            if (archiveBeforeTables)
                            {
                                var bytesReadSoFar = (uint)outputStream.Length;
                                var bytesToRead = (int)(entry.FilePosition - bytesReadSoFar);
                                binaryWriter.Write(binaryReader.ReadBytes(bytesToRead));
                                bytesReadSoFar += (uint)bytesToRead;

                                if (fileStream != null)
                                {
                                    using (var fileReader = new BinaryReader(fileStream))
                                    {
                                        binaryWriter.Write(fileReader.ReadBytes((int)fileLength));
                                    }
                                }

                                binaryReader.ReadBytes((int)entry.CompressedSize);
                                bytesReadSoFar += entry.CompressedSize;

                                bytesToRead = (int)(header.HashTablePos - bytesReadSoFar);
                                binaryWriter.Write(binaryReader.ReadBytes(bytesToRead));
                            }

                            binaryWriter.Write(binaryReader.ReadBytes((int)(archive._hashTable.Size * MpqHash.Size)));
                            binaryReader.ReadBytes((int)(archive._blockTable.Size * MpqEntry.Size));

                            var blockTable = new BlockTable(archive._blockTable.Size);
                            foreach (var mpqEntry in archive)
                            {
                                var offset = (uint)mpqEntry.FileOffset;
                                var isReplacedEntry = offset == entry.FileOffset;
                                var fileOffset = offset + (offset > (uint)entry.FileOffset ? (uint)sizeDifference : 0);
                                var compressedSize = isReplacedEntry ? (uint)fileLength : mpqEntry.CompressedSize;
                                var fileSize = isReplacedEntry ? (uint)fileLength : mpqEntry.FileSize;
                                var flags = isReplacedEntry ? (mpqEntry.Flags & ~(MpqFileFlags.Compressed | MpqFileFlags.Encrypted | MpqFileFlags.BlockOffsetAdjustedKey)) : mpqEntry.Flags;

                                blockTable.Add(new MpqEntry((uint)headerOffset, fileOffset, compressedSize, fileSize, flags));
                            }

                            // archive.Dispose();
                            blockTable.SerializeTo(binaryWriter);

                            if (!archiveBeforeTables)
                            {
                                throw new NotImplementedException();
                            }
                            else if (archiveStream.Position != archiveStream.Length)
                            {
                                throw new Exception();
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Opens an <see cref="MpqEntry"/> in the <see cref="MpqArchive"/>.
        /// </summary>
        /// <param name="filename">The name of the <see cref="MpqEntry"/> to open.</param>
        /// <returns>An <see cref="MpqStream"/> that provides access to the <see cref="MpqEntry"/> corresponding to the given <paramref name="filename"/>.</returns>
        /// <exception cref="FileNotFoundException">Thrown when no <see cref="MpqEntry"/> corresponding to the given <paramref name="filename"/> exists.</exception>
        public MpqStream OpenFile(string filename)
        {
            var entry = FileExists(filename, out var index)
                ? this[index]
                : throw new FileNotFoundException($"File not found: {filename}");

            entry.Filename = filename;

            return new MpqStream(this, entry);
        }

        /// <summary>
        /// Opens an <see cref="MpqEntry"/> in the <see cref="MpqArchive"/>.
        /// </summary>
        /// <param name="entry">The <see cref="MpqEntry"/> to open.</param>
        /// <returns>An <see cref="MpqStream"/> that provides access to the <see cref="MpqEntry"/> <paramref name="entry"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the given <paramref name="entry"/> is null.</exception>
        public MpqStream OpenFile(MpqEntry entry)
        {
            return new MpqStream(this, entry ?? throw new ArgumentNullException(nameof(entry)));
        }

        /// <summary>
        /// Executes <see cref="AddFilenames(Stream, bool)"/> using the <see cref="ListFile"/> in this <see cref="MpqArchive"/>, if it exists.
        /// </summary>
        /// <returns>True if a <see cref="ListFile"/> exists, false otherwise.</returns>
        public bool AddListfileFilenames()
        {
            if (!AddFilename(ListFile.Key))
            {
                return false;
            }

            using (Stream s = OpenFile(ListFile.Key))
            {
                AddFilenames(s);
            }

            return true;
        }

        /// <summary>
        /// Executes <see cref="AddFilename(string)"/> for every string in the given <paramref name="stream"/>.
        /// </summary>
        /// <param name="stream">The <see cref="Stream"/> from which to read filenames.</param>
        /// <param name="leaveOpen">True to leave the <paramref name="stream"/> open after executing this method, false otherwise.</param>
        /// <returns>The amount of strings (including duplicates) from the given <paramref name="stream"/> for which an <see cref="MpqEntry"/> exists in the archive.</returns>
        public int AddFilenames(Stream stream, bool leaveOpen = false)
        {
            var filesFound = 0;
            using (var sr = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen))
            {
                while (!sr.EndOfStream)
                {
                    if (AddFilename(sr.ReadLine() ?? string.Empty))
                    {
                        filesFound++;
                    }
                }
            }

            return filesFound;
        }

        /// <summary>
        /// Tries to find the <see cref="MpqEntry"/> corresponding to the given <paramref name="filename"/>, and update its <see cref="MpqEntry.Filename"/> if it exists.
        /// </summary>
        /// <param name="filename">The name for which the corresponding <see cref="MpqEntry"/>'s <see cref="MpqEntry.Filename"/> must be updated.</param>
        /// <returns>True if an <see cref="MpqEntry"/> with the given <paramref name="filename"/> exists in this <see cref="MpqArchive"/>, false otherwise.</returns>
        public bool AddFilename(string filename)
        {
            if (!TryGetHashEntry(filename, out var hash))
            {
                return false;
            }

            _blockTable[hash.BlockIndex].Filename = filename;
            return true;
        }

        /// <summary>
        /// Tries to find the <see cref="MpqEntry"/> corresponding to the given <paramref name="filename"/>.
        /// </summary>
        /// <param name="filename">The name for which to check if a corresponding <see cref="MpqEntry"/> exists.</param>
        /// <returns>True if an <see cref="MpqEntry"/> with the given <paramref name="filename"/> exists in this <see cref="MpqArchive"/>, false otherwise.</returns>
        public bool FileExists(string filename)
        {
            return TryGetHashEntry(filename, out _);
        }

        /// <summary>
        /// Tries to find the <see cref="MpqEntry"/> corresponding to the given <paramref name="filename"/>.
        /// </summary>
        /// <param name="filename">The name for which to check if a corresponding <see cref="MpqEntry"/> exists.</param>
        /// <param name="entryIndex">The index of the found <see cref="MpqEntry"/>, or -1 if there is no entry corresponding to the given <paramref name="filename"/>.</param>
        /// <returns>True if an <see cref="MpqEntry"/> with the given <paramref name="filename"/> exists in this <see cref="MpqArchive"/>, false otherwise.</returns>
        public bool FileExists(string filename, out int entryIndex)
        {
            var exists = TryGetHashEntry(filename, out var hash);

            entryIndex = exists
                ? (int)hash.BlockIndex
                : -1;

            return exists;
        }

        /// <summary>
        /// Closes the <see cref="BaseStream"/>.
        /// </summary>
        public void Dispose()
        {
            _baseStream?.Close();
        }

        /// <inheritdoc/>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return (_blockTable as IEnumerable).GetEnumerator();
        }

        /// <inheritdoc/>
        IEnumerator<MpqEntry> IEnumerable<MpqEntry>.GetEnumerator()
        {
            foreach (var entry in _blockTable)
            {
                yield return entry;
            }
        }

        private static bool TryLocateMpqHeader(
            Stream sourceStream,
#if NETCOREAPP3_0
            [NotNullWhen(true)]
#endif
            out MpqHeader? mpqHeader,
            out long headerOffset)
        {
            sourceStream.Seek(0, SeekOrigin.Begin);
            using (var reader = new BinaryReader(sourceStream, new UTF8Encoding(), true))
            {
                for (headerOffset = 0; headerOffset < sourceStream.Length - MpqHeader.Size; headerOffset += PreArchiveAlignBytes)
                {
                    if (reader.ReadUInt32() == MpqHeader.MpqId)
                    {
                        sourceStream.Seek(-4, SeekOrigin.Current);
                        mpqHeader = MpqHeader.FromReader(reader);
                        mpqHeader.SetHeaderOffset(headerOffset);
                        return true;
                    }

                    sourceStream.Seek(PreArchiveAlignBytes - 4, SeekOrigin.Current);
                }
            }

            mpqHeader = null;
            headerOffset = -1;
            return false;
        }

        private static Stream AlignStream(Stream? stream, bool leaveOpen = false)
        {
            if (stream is null)
            {
                return new MemoryStream();
            }

            if (!stream.CanWrite)
            {
                var memoryStream = new MemoryStream();

                stream.Seek(0, SeekOrigin.Begin);
                stream.CopyTo(memoryStream);
                if (!leaveOpen)
                {
                    stream.Dispose();
                }

                return memoryStream;
            }

            stream.Seek(0, SeekOrigin.End);
            var i = (uint)stream.Position & (PreArchiveAlignBytes - 1);
            if (i > 0)
            {
                for (; i < PreArchiveAlignBytes; i++)
                {
                    stream.WriteByte(0);
                }
            }

            return stream;
        }

        private bool TryGetHashEntry(string filename, out MpqHash hash)
        {
            var index = StormBuffer.HashString(filename, 0);
            index &= _mpqHeader.HashTableSize - 1;
            var name1 = StormBuffer.HashString(filename, 0x100);
            var name2 = StormBuffer.HashString(filename, 0x200);

            for (var i = index; i < _hashTable.Size; ++i)
            {
                hash = _hashTable[i];
                if (hash.Name1 == name1 && hash.Name2 == name2)
                {
                    return true;
                }
            }

            for (uint i = 0; i < index; ++i)
            {
                hash = _hashTable[i];
                if (hash.Name1 == name1 && hash.Name2 == name2)
                {
                    return true;
                }
            }

            hash = default;
            return false;
        }

        /*private int TryGetHashEntry(int entryIndex, out MpqHash hash)
        {
            for (var i = 0; i < _hashTable.Size; i++)
            {
                if (_hashTable[i].BlockIndex == entryIndex)
                {
                    hash = _hashTable[i];
                    return i;
                }
            }

            hash = MpqHash.NULL;
            return -1;
        }*/

        /*private bool VerifyHeader()
        {
            return _mpqHeader.HashTableSize == _hashTable.Size
                && _mpqHeader.BlockTableSize == _blockTable.Size
                && _mpqHeader.BlockSize == _blockSize >> BlockSizeModifier;
        }*/

        private bool IsArchiveBeforeTables()
        {
            return _mpqHeader.DataOffset == MpqHeader.Size
                || _mpqHeader.HashTablePos != MpqHeader.Size;
        }

        /*private uint FindCollidingHashEntries( uint hashIndex, bool returnOnUnknown )
        {
            var count = (uint)0;
            var initial = hashIndex;
            for ( ; hashIndex >= 0; count++ )
            {
                if ( _hashtable[--hashIndex].IsEmpty() )
                {
                    return count;
                }
                else if ( returnOnUnknown && _blocktable[_hashtable[hashIndex].BlockIndex].Filename == null )
                {
                    return count;
                }
            }
            hashIndex = HashEntryMask;
            for ( ; hashIndex > initial; count++ )
            {
                if ( _hashtable[--hashIndex].IsEmpty() )
                {
                    return count;
                }
                else if ( returnOnUnknown && _blocktable[_hashtable[hashIndex].BlockIndex].Filename == null )
                {
                    return count;
                }
            }
            return count;
        }*/
    }
}