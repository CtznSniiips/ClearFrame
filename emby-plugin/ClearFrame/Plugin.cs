using System;
using System.Collections.Generic;
using System.IO;
using ClearFrame.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace ClearFrame
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public static readonly Guid PluginId = new Guid("53466c82-3729-46c2-bc83-27a549151940");

        /// <summary>Guards read-modify-write of <see cref="PluginConfiguration"/>.</summary>
        public static readonly object ConfigLock = new object();

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        public override string Name => "ClearFrame";

        public override string Description =>
            "Home screen backend for the ClearFrame Samsung TV app: Continue Watching, Next Up, Live TV and sports, " +
            "Seerr discovery and requests, and one-press playback on paired TVs.";

        public override Guid Id => PluginId;

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.png");
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "clearframe",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.clearframe.html",
                    IsMainConfigPage = true,
                },
                new PluginPageInfo
                {
                    Name = "clearframejs",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.clearframe.js",
                },
            };
        }

        /// <summary>Applies a change to the configuration under a lock and saves it.</summary>
        public void Mutate(Action<PluginConfiguration> change)
        {
            lock (ConfigLock)
            {
                change(Configuration);
                SaveConfiguration();
            }
        }
    }
}
