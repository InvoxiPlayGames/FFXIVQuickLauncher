﻿using XIVLauncher.Common;
using XIVLauncher.Common.Game.Patch;
using XIVLauncher.Common.Game.Patch.Acquisition;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.NativeAOT;

public class RepairProgress
{
    public string CurrentStep { get; private set; } = "";
    public string CurrentFile { get; private set; } = "";
    public long Total { get; private set; } = 100;
    public long Progress { get; private set; } = 0;
    public long Speed { get; private set; } = 0;

    public RepairProgress()
    {
    }

    public RepairProgress(PatchVerifier? verify)
    {
        if (verify is null)
            return;

        switch (verify.State)
        {
            case PatchVerifier.VerifyState.DownloadMeta:
                CurrentStep = "Downloading meta files...";
                CurrentFile = $"{verify.CurrentFile}";
                Total = verify.Total;
                Progress = verify.Progress;
                Speed = verify.Speed;
                break;

            case PatchVerifier.VerifyState.VerifyAndRepair:
                CurrentStep = verify.CurrentMetaInstallState switch
                {
                    Common.Patching.IndexedZiPatch.IndexedZiPatchInstaller.InstallTaskState.NotStarted => "Verifying game files...",
                    _ => "Repairing game files...",
                };

                CurrentFile = $"{verify.CurrentFile}";
                Total = verify.Total;
                Progress = verify.Progress;
                Speed = verify.Speed;
                break;

            default:
                return;
        }
    }
}