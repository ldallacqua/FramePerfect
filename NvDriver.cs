using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace XivDlssFix;

/// <summary>
/// Read-only NVAPI queries about G-Sync: the live VRR state of a display (NvAPI_Disp_GetVRRInfo) and the
/// G-Sync fields of the game's driver profile (NvAPI_DRS_*, i.e. what NVIDIA Control Panel / Profile Inspector saved).
/// Nothing here writes driver settings. IDs and layouts are from github.com/NVIDIA/nvapi (nvapi.h, nvapi_interface.h,
/// NvApiDriverSettings.h).
/// </summary>
internal static unsafe class NvDriver
{
    // NVAPI function IDs
    private const uint IdInitialize = 0x0150E828;
    private const uint IdGetDisplayIdByDisplayName = 0xAE457190;
    private const uint IdGetVrrInfo = 0xDF8FDA57;
    private const uint IdDrsCreateSession = 0x0694D52E;
    private const uint IdDrsDestroySession = 0xDAD9CFF8;
    private const uint IdDrsLoadSettings = 0x375DBD6B;
    private const uint IdDrsFindApplicationByName = 0xEEE566B2;
    private const uint IdDrsGetProfileInfo = 0x61CD6FD6;
    private const uint IdDrsGetSetting = 0x73BF8338;
    private const uint IdDrsGetCurrentGlobalProfile = 0x617BFF9F;

    // Driver setting IDs (NvApiDriverSettings.h). Profile Inspector names in brackets.
    public const uint VrrAppOverride = 0x10A879CF;   // "G-SYNC" [G-SYNC - Application State]: 0 allow, 1 force off, 2 disallow, 3 ULMB, 4 fixed refresh
    public const uint VrrMode = 0x1194F158;          // "Enable G-SYNC globally" [G-SYNC - Application Mode]: 0 off, 1 full screen, 2 full screen and windowed
    public const uint VrrRequestState = 0x1094F1F7;  // "VRR requested state" [G-SYNC - Application Requested State]: same values

    private const int SettingSize = 12320;           // NVDRS_SETTING_V1 as the driver accepts it
    private const int SettingIdOffset = 4100;
    private const int SettingLocationOffset = 4108;  // 0 this profile, 1 global, 2 base, 3 default
    private const int SettingIsCurrentPredefinedOffset = 4112;
    private const int SettingCurrentValueOffset = 8220;
    private const int ApplicationSize = 20492;       // NVDRS_APPLICATION_V4
    private const int ProfileSize = 4116;            // NVDRS_PROFILE_V1

    private static readonly object Gate = new();
    private static readonly Dictionary<string, uint> DisplayIds = new();
    private static bool tried;
    private static delegate* unmanaged<uint, nint> query;

    public readonly record struct VrrInfo(int Status, bool Active, bool Possible, bool Requested, bool IndicatorOn, bool DisplayInVrrMode);

    public readonly record struct Setting(int Status, uint Value, int Location, bool UserSet)
    {
        public bool Found => this.Status == 0;

        public bool InProfile => this.Found && this.Location == 0;
    }

    /// <summary>NVAPI_SETTING_NOT_FOUND, for settings that weren't read.</summary>
    public static readonly Setting NotSet = new(-160, 0, -1, false);

    public sealed record Profile(int Status, string Name, Setting AppOverride, Setting AppMode, Setting AppRequested, Setting GlobalMode, Setting GlobalRequested);

    private static bool Ready()
    {
        lock (Gate)
        {
            if (tried)
                return query != null;
            tried = true;
            if (!NativeLibrary.TryLoad("nvapi64.dll", out var lib) || !NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out var qi))
                return false;
            var q = (delegate* unmanaged<uint, nint>)qi;
            var init = (delegate* unmanaged<int>)q(IdInitialize);
            if (init == null || init() != 0)
                return false;
            query = q;
            return true;
        }
    }

    /// <summary>Live VRR state of a display, by GDI name (e.g. \\.\DISPLAY1). Null when NVAPI or the display isn't available.</summary>
    public static VrrInfo? GetVrr(string gdiName)
    {
        if (!Ready())
            return null;
        var getVrr = (delegate* unmanaged<uint, byte*, int>)query(IdGetVrrInfo);
        if (getVrr == null)
            return null;

        uint id;
        lock (Gate)
        {
            if (!DisplayIds.TryGetValue(gdiName, out id))
            {
                var getId = (delegate* unmanaged<byte*, uint*, int>)query(IdGetDisplayIdByDisplayName);
                if (getId == null)
                    return null;
                var name = Encoding.ASCII.GetBytes(gdiName + "\0");
                int st;
                fixed (byte* p = name)
                    st = getId(p, &id);
                if (st != 0)
                    return new VrrInfo(st, false, false, false, false, false);
                DisplayIds[gdiName] = id;
            }
        }

        var v = stackalloc byte[24]; // NV_GET_VRR_INFO_V1
        new Span<byte>(v, 24).Clear();
        *(uint*)v = 24u | (1u << 16);
        var status = getVrr(id, v);
        if (status != 0)
        {
            lock (Gate)
                DisplayIds.Remove(gdiName); // display ids change when monitors are re-plugged
            return new VrrInfo(status, false, false, false, false, false);
        }

        var bits = *(uint*)(v + 4);
        return new VrrInfo(0, (bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0, (bits & 16) != 0);
    }

    /// <summary>The G-Sync fields of the driver profile that applies to <paramref name="exeName"/>, plus the global ones.</summary>
    public static Profile? ReadProfile(string exeName)
    {
        if (!Ready())
            return null;
        var createSession = (delegate* unmanaged<nint*, int>)query(IdDrsCreateSession);
        var destroySession = (delegate* unmanaged<nint, int>)query(IdDrsDestroySession);
        var loadSettings = (delegate* unmanaged<nint, int>)query(IdDrsLoadSettings);
        var findApp = (delegate* unmanaged<nint, char*, nint*, byte*, int>)query(IdDrsFindApplicationByName);
        var getProfileInfo = (delegate* unmanaged<nint, nint, byte*, int>)query(IdDrsGetProfileInfo);
        var getSetting = (delegate* unmanaged<nint, nint, uint, byte*, int>)query(IdDrsGetSetting);
        var getGlobal = (delegate* unmanaged<nint, nint*, int>)query(IdDrsGetCurrentGlobalProfile);
        if (createSession == null || destroySession == null || loadSettings == null || findApp == null || getProfileInfo == null || getSetting == null || getGlobal == null)
            return null;

        nint s0;
        var st = createSession(&s0);
        var session = s0;
        if (st != 0)
            return new Profile(st, "?", NotSet, NotSet, NotSet, NotSet, NotSet);

        var buf = (byte*)NativeMemory.AllocZeroed(SettingSize + ApplicationSize + ProfileSize);
        try
        {
            st = loadSettings(session);
            if (st != 0)
                return new Profile(st, "?", NotSet, NotSet, NotSet, NotSet, NotSet);

            Setting Get(nint profile, uint settingId)
            {
                new Span<byte>(buf, SettingSize).Clear();
                *(uint*)buf = SettingSize | (1u << 16);
                var s = getSetting(session, profile, settingId, buf);
                return s != 0
                    ? new Setting(s, 0, -1, false)
                    : new Setting(0, *(uint*)(buf + SettingCurrentValueOffset), *(int*)(buf + SettingLocationOffset), *(uint*)(buf + SettingIsCurrentPredefinedOffset) == 0);
            }

            nint global;
            Setting globalMode = NotSet, globalRequested = NotSet;
            if (getGlobal(session, &global) == 0)
            {
                globalMode = Get(global, VrrMode);
                globalRequested = Get(global, VrrRequestState);
            }

            var app = buf + SettingSize;
            *(uint*)app = ApplicationSize | (4u << 16);
            nint appProfile;
            fixed (char* n = exeName + "\0")
                st = findApp(session, n, &appProfile, app);
            if (st != 0)
                return new Profile(st, "(no profile for this exe: global settings apply)", NotSet, NotSet, NotSet, globalMode, globalRequested);

            var prof = app + ApplicationSize;
            *(uint*)prof = ProfileSize | (1u << 16);
            var name = getProfileInfo(session, appProfile, prof) == 0 ? new string((char*)(prof + 4)) : "?";
            return new Profile(0, name, Get(appProfile, VrrAppOverride), Get(appProfile, VrrMode), Get(appProfile, VrrRequestState), globalMode, globalRequested);
        }
        finally
        {
            NativeMemory.Free(buf);
            destroySession(session);
        }
    }

    public static string OverrideName(uint v) => v switch
    {
        0 => "G-SYNC",
        1 => "force off",
        2 => "disallow",
        3 => "ULMB",
        4 => "Fixed Refresh",
        _ => $"0x{v:X}",
    };

    public static string ModeName(uint v) => v switch
    {
        0 => "off",
        1 => "full screen only",
        2 => "full screen and windowed",
        _ => $"0x{v:X}",
    };

    /// <param name="ownProfile">What location 0 ("the profile that was asked") means for this setting.</param>
    public static string Describe(Setting s, Func<uint, string> name, string ownProfile = "game profile")
    {
        if (!s.Found)
            return s.Status == -160 ? "not set" : $"error {s.Status}";
        var where = s.Location switch { 0 => ownProfile, 1 => "global", 2 => "base profile", 3 => "driver default", _ => "?" };
        return $"{name(s.Value)} ({where}{(s.InProfile ? s.UserSet ? ", user-set" : ", NVIDIA preset" : string.Empty)})";
    }
}
