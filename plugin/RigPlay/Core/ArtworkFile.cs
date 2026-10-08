// SPDX-License-Identifier: GPL-3.0-only
// ArtworkFile.cs: the primary tablet's now-playing artwork (docs/protocol.md §6.14) as a file a SimHub dashboard's image
// element can show: %TEMP%\rigPlay\artwork.jpg or artwork.png, exposed as RigPlay.NowPlaying.ArtworkPath (§16.1).
// A new image is written next to the file and swapped in (File.Replace), so a dashboard never reads half an image.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.IO;
using RigPlayPlugin.Protocol;

namespace RigPlayPlugin
{
    public sealed class ArtworkFile
    {
        private readonly object sync = new object();
        private ArtworkMessage written;

        /// <param name="directory">Where the file goes; null for %TEMP%\rigPlay.</param>
        public ArtworkFile(string directory = null)
        {
            Directory = directory ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rigPlay");
        }

        public string Directory { get; }

        /// <summary>The current file; "" when there is no artwork.</summary>
        public string Path { get; private set; } = "";

        /// <summary>Images written so far (each new image counts once).</summary>
        public int Writes { get; private set; }

        public static string FileName(string mime)
        {
            return mime == ArtworkFormats.Png ? "artwork.png" : "artwork.jpg";
        }

        /// <summary>
        /// Shows <paramref name="artwork"/> (null: no artwork, e.g. no primary tablet). Does nothing when it is the message
        /// already shown, so it can be called on every surface update. Returns the path.
        /// </summary>
        public string Show(ArtworkMessage artwork)
        {
            lock (sync)
            {
                if (ReferenceEquals(artwork, written)) return Path;
                try
                {
                    if (artwork == null || artwork.Bytes == null)
                    {
                        Delete(FileName(ArtworkFormats.Jpeg));
                        Delete(FileName(ArtworkFormats.Png));
                        Path = "";
                        written = null;
                        return Path;
                    }
                    System.IO.Directory.CreateDirectory(Directory);
                    var name = FileName(artwork.Mime);
                    var target = System.IO.Path.Combine(Directory, name);
                    var temp = target + ".tmp";
                    File.WriteAllBytes(temp, artwork.Bytes);
                    Swap(temp, target);
                    Delete(FileName(artwork.Mime == ArtworkFormats.Png ? ArtworkFormats.Jpeg : ArtworkFormats.Png));
                    Path = target;
                    written = artwork;
                    Writes++;
                }
                catch (Exception ex)
                {
                    // `written` is unchanged, so the next surface update tries again.
                    PluginLog.Warn("Could not write the artwork file in " + Directory + ": " + ex.Message);
                }
                return Path;
            }
        }

        private static void Swap(string temp, string target)
        {
            try
            {
                if (File.Exists(target)) File.Replace(temp, target, null);
                else File.Move(temp, target);
            }
            catch (IOException)
            {
                // A reader holding the file without sharing: overwrite in place instead, which it tolerates better.
                File.Copy(temp, target, true);
                try { File.Delete(temp); } catch (Exception) { }
            }
        }

        private void Delete(string name)
        {
            try
            {
                var file = System.IO.Path.Combine(Directory, name);
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex)
            {
                PluginLog.Debug("Could not delete the old artwork " + name + ": " + ex.Message);
            }
        }
    }
}
