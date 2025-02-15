#nullable disable

namespace StardewModdingAPI.Web.Framework.Compression
{
    /// <summary>Handles GZip compression logic.</summary>
    internal interface IGzipHelper
    {
        /*********
        ** Methods
        *********/
        /// <summary>Compress a string.</summary>
        /// <param name="text">The text to compress.</param>
        string CompressString(string text);

        /// <summary>Decompress a string.</summary>
        /// <param name="rawText">The compressed text.</param>
        string DecompressString(string rawText);
    }
}
