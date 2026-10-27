using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamTune
{
    /// <summary>
    /// 直接调用 steam_api64.dll 的官方接口：不注入、不修改 Steam 客户端，
    /// 只使用 ISteamFriends::SetRichPresence。
    /// 注意：新版 SDK 的 SteamAPI_Init 是头文件里的 inline 函数，DLL 里只有
    /// SteamAPI_InitFlat / SteamInternal_SteamAPI_Init，所以这里两种都尝试。
    /// </summary>
    internal sealed class SteamNative : IDisposable
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32")]
        private static extern bool FreeLibrary(IntPtr module);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool InitDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool InitFlatDelegate(IntPtr errorMessage);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ShutdownDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RunCallbacksDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr AcquireInterfaceDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool SetRichPresenceDelegate(IntPtr self, IntPtr key, IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetPersonaNameDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool RestartAppDelegate(uint appId);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong GetSteamIdDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int RichPresenceKeyCountDelegate(IntPtr self, ulong steamId);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr RichPresenceKeyDelegate(IntPtr self, ulong steamId, int index);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr RichPresenceValueDelegate(IntPtr self, ulong steamId, IntPtr key);

        private static readonly string[] FriendsVersions = new string[]
        {
            "SteamAPI_SteamFriends_v018",
            "SteamAPI_SteamFriends_v017",
            "SteamAPI_SteamFriends_v016",
            "SteamAPI_SteamFriends_v015",
            "SteamAPI_SteamFriends_v014",
            "SteamAPI_SteamFriends_v013",
        };

        private IntPtr library = IntPtr.Zero;
        private IntPtr friends = IntPtr.Zero;
        private ShutdownDelegate shutdown;
        private RunCallbacksDelegate runCallbacks;
        private SetRichPresenceDelegate setRichPresence;
        private GetPersonaNameDelegate getPersonaName;

        public bool Available { get; private set; }
        public bool Initialized { get; private set; }
        public string Error { get; private set; }
        public string PersonaName { get; private set; }
        public int AppId { get; set; }

        public bool Initialize(int appId, string baseDirectory)
        {
            AppId = appId;
            string dllPath = Path.Combine(baseDirectory, "steam_api64.dll");
            if (!File.Exists(dllPath))
            {
                Error = "缺少 steam_api64.dll";
                return false;
            }

            if (appId > 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamAppId")))
            {
                Environment.SetEnvironmentVariable("SteamAppId", appId.ToString());
            }

            library = LoadLibraryW(dllPath);
            if (library == IntPtr.Zero)
            {
                Error = "无法加载 steam_api64.dll（错误码 " + Marshal.GetLastWin32Error() + "）";
                return false;
            }

            string initError;
            if (!CallInit(out initError))
            {
                Error = initError;
                return false;
            }

            IntPtr shutdownPtr = GetProcAddress(library, "SteamAPI_Shutdown");
            IntPtr callbacksPtr = GetProcAddress(library, "SteamAPI_RunCallbacks");
            IntPtr setRpPtr = GetProcAddress(library, "SteamAPI_ISteamFriends_SetRichPresence");
            IntPtr personaPtr = GetProcAddress(library, "SteamAPI_ISteamFriends_GetPersonaName");
            if (shutdownPtr == IntPtr.Zero || callbacksPtr == IntPtr.Zero ||
                setRpPtr == IntPtr.Zero || personaPtr == IntPtr.Zero)
            {
                Error = "steam_api64.dll 版本不受支持（缺少 friends flat API）";
                return false;
            }

            shutdown = (ShutdownDelegate)Marshal.GetDelegateForFunctionPointer(shutdownPtr, typeof(ShutdownDelegate));
            runCallbacks = (RunCallbacksDelegate)Marshal.GetDelegateForFunctionPointer(callbacksPtr, typeof(RunCallbacksDelegate));
            setRichPresence = (SetRichPresenceDelegate)Marshal.GetDelegateForFunctionPointer(setRpPtr, typeof(SetRichPresenceDelegate));
            getPersonaName = (GetPersonaNameDelegate)Marshal.GetDelegateForFunctionPointer(personaPtr, typeof(GetPersonaNameDelegate));

            friends = IntPtr.Zero;
            for (int i = 0; i < FriendsVersions.Length; i++)
            {
                IntPtr ptr = GetProcAddress(library, FriendsVersions[i]);
                if (ptr == IntPtr.Zero) continue;
                AcquireInterfaceDelegate acquire = (AcquireInterfaceDelegate)Marshal.GetDelegateForFunctionPointer(ptr, typeof(AcquireInterfaceDelegate));
                friends = acquire();
                if (friends != IntPtr.Zero) break;
            }
            if (friends == IntPtr.Zero)
            {
                Error = "找不到 ISteamFriends 接口";
                return false;
            }

            Initialized = true;
            Available = true;
            Error = null;
            try
            {
                IntPtr namePtr = getPersonaName(friends);
                PersonaName = namePtr == IntPtr.Zero ? null : ReadUtf8(namePtr);
            }
            catch
            {
                PersonaName = null;
            }
            return true;
        }

        /// <summary>Steam 接口返回的是 UTF-8 字符串，不能按 ANSI 解</summary>
        private static string ReadUtf8(IntPtr pointer)
        {
            int length = 0;
            while (Marshal.ReadByte(pointer, length) != 0) length++;
            if (length == 0) return string.Empty;
            byte[] buffer = new byte[length];
            Marshal.Copy(pointer, buffer, 0, length);
            return Encoding.UTF8.GetString(buffer);
        }

        private bool CallInit(out string error)
        {
            error = null;

            // 不同 SDK 版本导出的初始化入口不一样，按可用性依次尝试
            IntPtr plainPtr = GetProcAddress(library, "SteamAPI_Init");
            if (plainPtr != IntPtr.Zero)
            {
                InitDelegate plain = (InitDelegate)Marshal.GetDelegateForFunctionPointer(plainPtr, typeof(InitDelegate));
                if (plain()) return true;
            }

            IntPtr safePtr = GetProcAddress(library, "SteamAPI_InitSafe");
            if (safePtr != IntPtr.Zero)
            {
                InitDelegate safe = (InitDelegate)Marshal.GetDelegateForFunctionPointer(safePtr, typeof(InitDelegate));
                if (safe()) return true;
            }

            IntPtr flatPtr = GetProcAddress(library, "SteamAPI_InitFlat");
            if (flatPtr != IntPtr.Zero)
            {
                InitFlatDelegate flat = (InitFlatDelegate)Marshal.GetDelegateForFunctionPointer(flatPtr, typeof(InitFlatDelegate));
                IntPtr buffer = Marshal.AllocHGlobal(1024);
                try
                {
                    for (int i = 0; i < 1024; i++) Marshal.WriteByte(buffer, i, 0);
                    if (flat(buffer)) return true;
                    string message = Marshal.PtrToStringAnsi(buffer);
                    if (!string.IsNullOrEmpty(message))
                    {
                        error = message;
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            if (plainPtr == IntPtr.Zero && safePtr == IntPtr.Zero && flatPtr == IntPtr.Zero)
            {
                error = "steam_api64.dll 缺少初始化入口";
                return false;
            }

            error = "SteamAPI 初始化失败（需要由 Steam 启动，且 AppID 已授权）";
            return false;
        }

        /// <summary>Steam 接口全部使用 UTF-8 字符串，这里手动编组，避免走 ANSI 代码页</summary>
        private static IntPtr Utf8Alloc(string text)
        {
            if (text == null) return IntPtr.Zero;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            IntPtr buffer = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            Marshal.WriteByte(buffer, bytes.Length, 0);
            return buffer;
        }

        public bool SetPresence(string key, string value)
        {
            if (!Available) return false;
            IntPtr keyPtr = IntPtr.Zero;
            IntPtr valuePtr = IntPtr.Zero;
            try
            {
                keyPtr = Utf8Alloc(key);
                valuePtr = Utf8Alloc(value);
                return setRichPresence(friends, keyPtr, valuePtr);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (keyPtr != IntPtr.Zero) Marshal.FreeHGlobal(keyPtr);
                if (valuePtr != IntPtr.Zero) Marshal.FreeHGlobal(valuePtr);
            }
        }

        /// <summary>
        /// 读回自己账号的 rich presence。用来确认“Steam 到底收到了什么”，
        /// 有些客户端版本对本人不返回数据，此时返回空字典。
        /// </summary>
        public System.Collections.Generic.Dictionary<string, string> ReadOwnRichPresence()
        {
            System.Collections.Generic.Dictionary<string, string> result =
                new System.Collections.Generic.Dictionary<string, string>();
            if (!Available || friends == IntPtr.Zero) return result;

            try
            {
                IntPtr userPtr = GetProcAddress(library, "SteamAPI_SteamUser_v023");
                if (userPtr == IntPtr.Zero) userPtr = GetProcAddress(library, "SteamAPI_SteamUser_v022");
                IntPtr getIdPtr = GetProcAddress(library, "SteamAPI_ISteamUser_GetSteamID");
                IntPtr countPtr = GetProcAddress(library, "SteamAPI_ISteamFriends_GetFriendRichPresenceKeyCount");
                IntPtr keyPtr = GetProcAddress(library, "SteamAPI_ISteamFriends_GetFriendRichPresenceKeyByIndex");
                IntPtr valuePtr = GetProcAddress(library, "SteamAPI_ISteamFriends_GetFriendRichPresence");
                if (userPtr == IntPtr.Zero || getIdPtr == IntPtr.Zero || countPtr == IntPtr.Zero ||
                    keyPtr == IntPtr.Zero || valuePtr == IntPtr.Zero)
                {
                    return result;
                }

                AcquireInterfaceDelegate acquireUser = (AcquireInterfaceDelegate)Marshal.GetDelegateForFunctionPointer(userPtr, typeof(AcquireInterfaceDelegate));
                GetSteamIdDelegate getSteamId = (GetSteamIdDelegate)Marshal.GetDelegateForFunctionPointer(getIdPtr, typeof(GetSteamIdDelegate));
                RichPresenceKeyCountDelegate countFn = (RichPresenceKeyCountDelegate)Marshal.GetDelegateForFunctionPointer(countPtr, typeof(RichPresenceKeyCountDelegate));
                RichPresenceKeyDelegate keyFn = (RichPresenceKeyDelegate)Marshal.GetDelegateForFunctionPointer(keyPtr, typeof(RichPresenceKeyDelegate));
                RichPresenceValueDelegate valueFn = (RichPresenceValueDelegate)Marshal.GetDelegateForFunctionPointer(valuePtr, typeof(RichPresenceValueDelegate));

                IntPtr user = acquireUser();
                if (user == IntPtr.Zero) return result;
                ulong steamId = getSteamId(user);
                if (steamId == 0) return result;

                int count = countFn(friends, steamId);
                for (int i = 0; i < count && i < 32; i++)
                {
                    IntPtr keyNamePtr = keyFn(friends, steamId, i);
                    if (keyNamePtr == IntPtr.Zero) continue;
                    string key = ReadUtf8(keyNamePtr);
                    if (string.IsNullOrEmpty(key)) continue;
                    IntPtr keyBuffer = Utf8Alloc(key);
                    try
                    {
                        IntPtr valuePtrValue = valueFn(friends, steamId, keyBuffer);
                        result[key] = valuePtrValue == IntPtr.Zero ? string.Empty : ReadUtf8(valuePtrValue);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(keyBuffer);
                    }
                }
            }
            catch
            {
                // 读不到就当没有，不影响发布
            }
            return result;
        }

        public void Tick()
        {
            if (!Available) return;
            try
            {
                runCallbacks();
            }
            catch
            {
            }
        }

        /// <summary>未通过 Steam 启动时把用户拉回 Steam 启动路径（发布版使用）</summary>
        public static bool RestartThroughSteam(uint appId, string baseDirectory)
        {
            string dllPath = Path.Combine(baseDirectory, "steam_api64.dll");
            if (!File.Exists(dllPath)) return false;
            IntPtr module = LoadLibraryW(dllPath);
            if (module == IntPtr.Zero) return false;
            try
            {
                IntPtr ptr = GetProcAddress(module, "SteamAPI_RestartAppIfNecessary");
                if (ptr == IntPtr.Zero) return false;
                RestartAppDelegate restart = (RestartAppDelegate)Marshal.GetDelegateForFunctionPointer(ptr, typeof(RestartAppDelegate));
                return restart(appId);
            }
            finally
            {
                FreeLibrary(module);
            }
        }

        public void Dispose()
        {
            if (Initialized)
            {
                try
                {
                    shutdown();
                }
                catch
                {
                }
                Initialized = false;
            }
            if (library != IntPtr.Zero)
            {
                FreeLibrary(library);
                library = IntPtr.Zero;
            }
            Available = false;
        }
    }
}
