using System;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.NTE.Management.PresetConfig;
using Hi3Helper.Plugin.NTE.Utils;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.NTE;

[GeneratedComClass]
public partial class NtePlugin : PluginBase
{
    private static readonly IPluginPresetConfig[] PresetConfigInstances =
    [
        new NteCnPresetConfig()
    ];

    private static DateTime _pluginCreationDate = new(2026, 02, 01, 00, 00, 0, DateTimeKind.Utc);

    public override void GetPluginName(out string result)
    {
        result = "Neverness To Everness Plugin";
    }

    public override void GetPluginDescription(out string result)
    {
        result = "A plugin for Neverness To Everness (异环) in Collapse Launcher";
    }

    public override void GetPluginAuthor(out string result)
    {
        result = "CollapsePlugin";
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
        SharedStatic.InstanceLogger.LogInformation("[NTE] Starting execution...");
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
        result = Convert.ToBase64String(NteImageData.NteAppIconData);
    }

    public override void GetNotificationPosterUrl(out string result)
    {
        result = Convert.ToBase64String(NteImageData.NtePosterData);
    }
}
