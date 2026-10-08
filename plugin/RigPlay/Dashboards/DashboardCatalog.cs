// SPDX-License-Identifier: GPL-3.0-only
// DashboardCatalog.cs: the dashboards installed in SimHub and the URLs tablets load them from (docs/protocol.md §11).
// A dashboard is a folder under <SimHub>\DashTemplates\ holding a .djson file; its name is the folder name, and
// <folder>.djson.metadata may carry a display title. The URL is http://<localIp>:<webDashPort>/Dash#<name>
// (/dashboard/<name> is a 404 on SimHub 9.12.6; see DashboardUrls.DashPagePrefix).
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;

namespace RigPlayPlugin.Dashboards
{
    public sealed class DashboardInfo
    {
        public DashboardInfo(string name, string title)
        {
            Name = name;
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        }

        /// <summary>Folder name: what the URL uses.</summary>
        public string Name { get; }

        /// <summary>Title from the .metadata file, or null.</summary>
        public string Title { get; }

        /// <summary>What the combo shows: the title, with the folder name when they differ.</summary>
        public string Display => Title == null || string.Equals(Title, Name, StringComparison.OrdinalIgnoreCase) ? Name : Title + " (" + Name + ")";

        public override string ToString()
        {
            return Display;
        }
    }

    public static class DashboardCatalog
    {
        public const string DashTemplatesFolder = "DashTemplates";

        /// <summary>The first candidate directory that has a DashTemplates folder, or null.</summary>
        public static string FindSimHubDir(IEnumerable<string> candidates)
        {
            foreach (var dir in candidates)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    if (Directory.Exists(Path.Combine(dir, DashTemplatesFolder))) return dir;
                }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>The dashboards under <paramref name="dashTemplatesDir"/>, sorted by display name. Never throws.</summary>
        public static List<DashboardInfo> Enumerate(string dashTemplatesDir)
        {
            var list = new List<DashboardInfo>();
            if (string.IsNullOrEmpty(dashTemplatesDir)) return list;
            string[] folders;
            try
            {
                if (!Directory.Exists(dashTemplatesDir)) return list;
                folders = Directory.GetDirectories(dashTemplatesDir);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Listing " + dashTemplatesDir + " failed: " + ex.Message);
                return list;
            }
            foreach (var folder in folders)
            {
                try
                {
                    var name = Path.GetFileName(folder);
                    var files = Directory.GetFiles(folder);
                    // Support folders (_Library, Ressources) hold no .djson and are not dashboards.
                    if (!files.Any(f => f.EndsWith(".djson", StringComparison.OrdinalIgnoreCase))) continue;
                    var metadata = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), name + ".djson.metadata", StringComparison.OrdinalIgnoreCase));
                    string title = null;
                    if (metadata != null)
                    {
                        try { title = ReadTitle(File.ReadAllText(metadata)); } catch (Exception) { }
                    }
                    list.Add(new DashboardInfo(name, title));
                }
                catch (Exception ex)
                {
                    PluginLog.Debug("Skipping dashboard folder " + folder + ": " + ex.Message);
                }
            }
            return list.OrderBy(d => d.Display, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The <c>Title</c> of a SimHub .djson.metadata file, or null when absent, null or unreadable.</summary>
        public static string ReadTitle(string metadataJson)
        {
            try
            {
                var o = JObject.Parse(metadataJson);
                var t = o["Title"];
                if (t == null || t.Type != JTokenType.String) return null;
                var s = ((string)t).Trim();
                return s.Length == 0 ? null : s;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public static class DashboardUrls
    {
        /// <summary>
        /// The page of SimHub's web dash server that renders one dashboard, before the encoded name.
        /// </summary>
        /// <remarks>
        /// SimHub 9.12.6 answers 404 for <c>/dashboard/&lt;name&gt;</c> (checked on the test VM for several dashboards).
        /// Its own dashboard list links to <c>/Dash#&lt;encodeURIComponent(name)&gt;</c>, which renders the dashboard in a
        /// browser; docs/protocol.md §11 specifies this form. The tablet treats the URL as opaque.
        /// </remarks>
        public const string DashPagePrefix = "/Dash#";

        /// <summary>
        /// http://&lt;localIp&gt;:&lt;port&gt;/Dash#&lt;name&gt; (spec §11, see <see cref="DashPagePrefix"/>): the PC's address on
        /// the tablet's connection (IPv4-mapped written as IPv4, IPv6 in brackets) and the folder name percent-encoded.
        /// Null when no dashboard is named.
        /// </summary>
        public static string Build(IPAddress localAddress, int port, string dashboardName)
        {
            if (string.IsNullOrWhiteSpace(dashboardName) || localAddress == null) return null;
            var address = Net.NetUtil.Normalize(localAddress);
            string host;
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var text = address.ToString();
                var percent = text.IndexOf('%');
                if (percent >= 0) text = text.Substring(0, percent) + "%25" + text.Substring(percent + 1);
                host = "[" + text + "]";
            }
            else
            {
                host = address.ToString();
            }
            return "http://" + host + ":" + port + DashPagePrefix + EscapeSegment(dashboardName);
        }

        /// <summary>Percent-encodes everything except RFC 3986 unreserved characters.</summary>
        public static string EscapeSegment(string segment)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(segment);
            var sb = new System.Text.StringBuilder(bytes.Length * 3);
            foreach (var b in bytes)
            {
                var c = (char)b;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~')
                    sb.Append(c);
                else
                    sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
