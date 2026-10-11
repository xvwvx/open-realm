using System.Collections.Generic;
using MdxPreview.Core.Mpq;

namespace MdxPreview.Core.Assets
{
    /// <summary>
    /// Exposes one MPQ archive as an <see cref="IFileSource"/> so archives and
    /// loose directories can be composed into a single lookup chain.
    /// </summary>
    public sealed class MpqFileSource : IFileSource
    {
        readonly MpqArchive _archive;

        public MpqFileSource(MpqArchive archive, int priority = 0)
        {
            _archive = archive;
            Priority = priority;
        }

        public string Name => _archive?.Name ?? "<no archive>";

        public int Priority { get; }

        public MpqArchive Archive => _archive;

        public bool TryRead(string virtualPath, out byte[] data)
        {
            if (_archive != null && _archive.TryReadFile(virtualPath, out data)) return true;
            data = null;
            return false;
        }

        public bool Exists(string virtualPath) => _archive != null && _archive.Contains(virtualPath);

        public IEnumerable<string> Enumerate(string mask)
            => _archive?.FindFiles(mask) ?? (IEnumerable<string>)new List<string>();

        public override string ToString() => $"MPQ:{Name}";
    }
}
