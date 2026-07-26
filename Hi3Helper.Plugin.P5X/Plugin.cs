using System;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.P5X.Management.PresetConfig;
using Hi3Helper.Plugin.P5X.Utils;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.P5X;

[GeneratedComClass]
public partial class P5xPlugin : PluginBase
{
    private static readonly IPluginPresetConfig[] PresetConfigInstances =
    [
        new P5xCnPresetConfig()
    ];

    private static DateTime _pluginCreationDate = new(2026, 02, 01, 00, 00, 0, DateTimeKind.Utc);

    public override void GetPluginName(out string result)
    {
        result = "Persona 5: The Phantom X Plugin";
    }

    public override void GetPluginDescription(out string result)
    {
        result = "A plugin for Persona 5: The Phantom X (P5X) in Collapse Launcher";
    }

    public override void GetPluginAuthor(out string result)
    {
        result = "Voheart";
    }

    public override unsafe void GetPluginCreationDate(out DateTime* result)
    {
        result = _pluginCreationDate.AsPointer();
    }

    public override void GetPresetConfigCount(out int count)
    {
        count = PresetConfigInstances.Length;
    }

    public override void GetPresetConfig(int index, out IPluginPresetConfig presetConfig)
    {
        SharedStatic.InstanceLogger.LogInformation("[P5X] Starting execution...");
        if (index < 0 || index >= PresetConfigInstances.Length)
        {
            presetConfig = null!;
            return;
        }

        presetConfig = PresetConfigInstances[index];
    }

    public override void GetPluginSelfUpdater(out IPluginSelfUpdate selfUpdate)
    {
        selfUpdate = new SelfUpdate();
    }

    public override void GetPluginAppIconUrl(out string result)
    {
        result = Convert.ToBase64String(P5xImageData.P5xAppIconData);
    }

    public override void GetNotificationPosterUrl(out string result)
    {
        result = Convert.ToBase64String(P5xImageData.P5xPosterData);
    }
}
