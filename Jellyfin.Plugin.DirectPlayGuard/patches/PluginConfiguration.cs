using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DirectPlayGuard.Configuration;

/// <summary>Configuration for Direct Play Guard.</summary>
[XmlRoot("DirectPlayGuardConfiguration")]
public sealed class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        Enabled = true;
        DisableVideoTranscoding = true;
        DisableAudioTranscoding = true;
        DisableRemuxing = false;
        ApplyToAdministrators = true;

        EnableWebMessage = true;
        MessageTitle = "Playback not supported on this device";
        MessageText = "This server does not transcode media. Your current client cannot play this file directly. Use a recommended Jellyfin app and set playback quality to Original or Maximum.";
        RecommendedApps = "Computer: Jellyfin Media Player\nApple devices: Swiftfin or Jellyfin for iOS\nAndroid/Google TV/Fire TV: Jellyfin for Android TV\nRoku: Jellyfin for Roku";
        HelpUrl = "https://jellyfin.org/clients/";

        ImageMode = "none";
        ImageUrl = string.Empty;
        UploadedImageFileName = string.Empty;
        ImageMaxHeight = 260;
        ImageFit = "contain";
        ImageCornerRadius = 12;

        ModalWidth = 640;
        AccentColor = "#00A4DC";
        TextAlignment = "center";
        ShowRecommendedApps = true;
        FooterText = string.Empty;

        PolicyBackups = new List<UserPolicyBackup>();
    }

    [XmlElement]
    public bool Enabled { get; set; }

    [XmlElement]
    public bool DisableVideoTranscoding { get; set; }

    [XmlElement]
    public bool DisableAudioTranscoding { get; set; }

    [XmlElement]
    public bool DisableRemuxing { get; set; }

    [XmlElement]
    public bool ApplyToAdministrators { get; set; }

    [XmlElement]
    public bool EnableWebMessage { get; set; }

    [XmlElement]
    public string MessageTitle { get; set; }

    [XmlElement]
    public string MessageText { get; set; }

    [XmlElement]
    public string RecommendedApps { get; set; }

    [XmlElement]
    public string HelpUrl { get; set; }

    /// <summary>none, upload, or url.</summary>
    [XmlElement]
    public string ImageMode { get; set; }

    [XmlElement]
    public string ImageUrl { get; set; }

    [XmlElement]
    public string UploadedImageFileName { get; set; }

    [XmlElement]
    public int ImageMaxHeight { get; set; }

    /// <summary>contain or cover.</summary>
    [XmlElement]
    public string ImageFit { get; set; }

    [XmlElement]
    public int ImageCornerRadius { get; set; }

    [XmlElement]
    public int ModalWidth { get; set; }

    [XmlElement]
    public string AccentColor { get; set; }

    /// <summary>left or center.</summary>
    [XmlElement]
    public string TextAlignment { get; set; }

    [XmlElement]
    public bool ShowRecommendedApps { get; set; }

    [XmlElement]
    public string FooterText { get; set; }

    /// <summary>
    /// Original permission values captured before this plugin changes a user.
    /// These allow settings to be restored when protection is disabled or the plugin is removed.
    /// </summary>
    [XmlArray("PolicyBackups")]
    [XmlArrayItem("User")]
    public List<UserPolicyBackup> PolicyBackups { get; set; }
}

/// <summary>Snapshot of the permissions Direct Play Guard can change.</summary>
public sealed class UserPolicyBackup
{
    [XmlAttribute]
    public Guid UserId { get; set; }

    [XmlElement]
    public bool VideoTranscoding { get; set; }

    [XmlElement]
    public bool AudioTranscoding { get; set; }

    [XmlElement]
    public bool Remuxing { get; set; }
}
