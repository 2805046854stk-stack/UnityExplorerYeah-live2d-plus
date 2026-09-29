using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace UnityExplorer.Runtime
{
    /// <summary>
    /// v16au：**FSB5 音频 bank 直读**兜底导出器。
    ///
    /// ============================ 为什么需要这个东西 ============================
    /// 现象：`AudioClip.GetData()` 返回 true，但缓冲区**全部为 0**，同时 Unity 原生层打印
    ///   `SoundManager.cpp(702) : Error executing result = instance->m_Sound->lock(...)
    ///    (An invalid parameter was passed to this function.)`
    ///
    /// 根因（已用官方资料 + 实机数据双重坐实）：
    ///   1) Unity 官方文档 `AudioClip.GetData`（2023.1+）原话：
    ///      "GetData doesn't work with streamed audio clips, **including clips streamed from the disk**,
    ///       and clips created with AudioClip.Create where the stream parameter has been set to true."
    ///   2) 官方 issue UUM-363「Cannot get data from streamed samples when using PCM read callbacks」：
    ///      2019.4 / 2020.3 / 2021.2 判定 **Won't Fix**，2023.1 只「改文档 + 改错误信息」，
    ///      并补记「**GetData 的返回值目前是未文档化的**」——这就是「明明读不到却返回 true」的来源。
    ///   3) 实机取证：把序列化资产解开看，每个 AudioClip 都带
    ///      `m_Resource = StreamedResource(m_Source='sharedassetsN.resource', m_Offset=…, m_Size=…)`
    ///      —— 数据根本不在内存里，`GetData` 必须去 `FMOD_Sound_Lock` 那个**不可 lock 的流式 sound**，
    ///      于是 FMOD 返回 `FMOD_ERR_INVALID_PARAM`，Unity 回填 0，2019.4 还谎报 true。
    ///      （实测两款游戏 332/332、409/409 个 AudioClip **全部**是这种形态，不是个例。）
    ///
    /// ============================ 解决办法 ============================
    /// 绕开 Unity 的 AudioClip API，直接读**游戏自己的** `.resource` 文件：
    ///   ① 从 `Application.dataPath` 下的 `*.resource` 里，从偏移 0 开始按 FSB5 头**逐段行走**切出每个 bank
    ///      （必须「行走」而不是裸扫 magic：实测游戏 A 的文件里 `FSB5` 出现 411 次但只有 409 个真 bank，
    ///        行走天然跳过数据内部的假 magic，并在遇到非 FSB5 处干净收尾）
    ///   ② 每个 bank 头自带长度：`size = 60 + sampleHeaderSize*numSamples + nameTableSize + dataSize`
    ///      （在 148 个 + 409 个真实 bank 上零误差验证过）
    ///   ③ 用随插件附带的 `fmod.dll`（FMOD Studio 2.02）把 bank 解成 PCM：
    ///      `create_sound(OPENMEMORY)` → `getSubSound(0)` → `getLength/GetFormat` → `Lock` → PCM
    ///      ★ 必须取 subsound：FSB 是容器，父 sound 的 format/length 全是空的（实测踩过）
    ///   ④ 把 AudioClip 与 bank 对应起来：**按序列对位**，不是按 (声道,采样率,帧数) 三元组匹配
    ///      —— 三元组严重碰撞（实测 148 个 clip 只有 88 个唯一三元组，84 个卷入碰撞，
    ///        `Out_P_1..7`、`cum_p_1..9` 这种同系列分支语音时长完全一致，靠三元组只能瞎猜）。
    ///      对位依据：**.assets 里 AudioClip 的序列化顺序 == 对应 .resource 里 bank 的偏移顺序**
    ///      （sharedassets0 上逐元素 148/148 完全一致）。
    ///      并且这个对位**自带硬校验**：整段序列必须逐元素相同，对不上就明确报错，绝不会静默错配。
    ///
    /// 该方法完全走托管层 + 文件 IO + 一个原生解码库，**不依赖任何 IL2CPP 内部结构**，
    /// 因此在 Mono / IL2CPP 各配置下都可用。
    /// </summary>
    public static class AudioBankExporter
    {
        // ==================================================================
        // 1. FMOD 原生绑定
        // ==================================================================

        /// <summary>FMOD Studio 2.02.48 的版本常量。版本不符时 System_Create 会返回 HEADER_MISMATCH(20)。</summary>
        private const uint FMOD_VERSION = 0x00020230;

        private const uint FMOD_OK = 0;
        private const uint FMOD_HEADER_MISMATCH = 20;

        /// <summary>OPENMEMORY —— 让 FMOD 直接从我们给的内存块读 bank。</summary>
        private const uint MODE_OPENMEMORY = 0x00000800;

        private const uint TIMEUNIT_PCM = 0x00000002;
        private const uint TIMEUNIT_PCMBYTES = 0x00000004;

        // SOUND_FORMAT
        private const int SOUNDFORMAT_PCM8 = 1;
        private const int SOUNDFORMAT_PCM16 = 2;
        private const int SOUNDFORMAT_PCM24 = 3;
        private const int SOUNDFORMAT_PCM32 = 4;
        private const int SOUNDFORMAT_PCMFLOAT = 5;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("fmod", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int FMOD_System_Create(out IntPtr system, uint version);

        [DllImport("fmod", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int FMOD_System_Init(IntPtr system, int maxChannels, uint flags, IntPtr extraDriverData);

        [DllImport("fmod", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int FMOD_System_Release(IntPtr system);

        [DllImport("fmod", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int FMOD_System_CreateSound(
            IntPtr system, IntPtr nameOrData, uint mode, ref CREATESOUNDEXINFO exinfo, out IntPtr sound);

        [DllImport("fmod", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int FMOD_Sound_GetNumSubSounds(IntPtr sound, out int numSubSounds);

        [DllImport("fmod", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int FMOD_Sound_GetSubSound(IntPtr sound, int index, out IntPtr subSound);

        [DllImport("fmod", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int FMOD_Sound_GetLength(IntPtr sound, out uint length, uint lengthType);

        [DllImport("fmod", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int FMOD_Sound_GetFormat(
            IntPtr sound, out int type, out int format, out int channels, out int bits);

        [DllImport("fmod", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int FMOD_Sound_GetDefaults(IntPtr sound, out float frequency, out int priority);

        [DllImport("fmod", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int FMOD_Sound_Lock(
            IntPtr sound, uint offset, uint length,
            out IntPtr ptr1, out IntPtr ptr2, out uint len1, out uint len2);

        [DllImport("fmod", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int FMOD_Sound_Unlock(IntPtr sound, IntPtr ptr1, IntPtr ptr2, uint len1, uint len2);

        [DllImport("fmod", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int FMOD_Sound_Release(IntPtr sound);

        /// <summary>
        /// FMOD Studio 2.02 的 CREATESOUNDEXINFO（x64，sizeof = 224）。
        /// 字段顺序/偏移已用 ctypes 实测核对：cbsize +0、length +4、numchannels +12、defaultfrequency +16。
        /// 其余字段用 OPENMEMORY 时不需要，但**必须保留占位**，否则 cbsize 与实际布局对不上，FMOD 会读错内存。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct CREATESOUNDEXINFO
        {
            public int cbsize;
            public uint length;
            public uint fileoffset;
            public int numchannels;
            public int defaultfrequency;
            public int format;
            public uint decodebuffersize;
            public int initialsubsound;
            public int numsubsounds;
            public IntPtr inclusionlist;
            public int inclusionlistnum;
            public IntPtr pcmreadcallback;
            public IntPtr pcmsetposcallback;
            public IntPtr nonblockcallback;
            public IntPtr dlsname;
            public IntPtr encryptionkey;
            public int maxpolyphony;
            public IntPtr userdata;
            public int suggestedsoundtype;
            public IntPtr useropen;
            public IntPtr userclose;
            public IntPtr userread;
            public IntPtr userseek;
            public IntPtr userasyncread;
            public IntPtr userasynccancel;
            public IntPtr fileuserdata;
            public int filebuffersize;
            public int channelorder;
            public IntPtr initialsoundgroup;
            public uint initialseekposition;
            public int initialseekpostype;
            public int ignoresetfilesystem;
            public uint audioqueuepolicy;
            public uint minmidigranularity;
            public int nonblockthreadid;
            public IntPtr fsbguid;
        }

        // ---------------- FMOD 会话（全局单例） ----------------

        private static IntPtr _fmodSystem = IntPtr.Zero;
        private static bool _fmodInitTried;
        private static bool _fmodAvailable;
        private static string _fmodStatus = "未初始化";

        /// <summary>fmod.dll 是否已成功加载并初始化。</summary>
        public static bool FmodAvailable { get { return _fmodAvailable; } }
        public static string FmodStatus { get { return _fmodStatus; } }

        private static string PluginDirectory()
        {
            try
            {
                string asm = typeof(AudioBankExporter).Assembly.Location;
                if (!string.IsNullOrEmpty(asm))
                    return Path.GetDirectoryName(asm);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 加载随插件分发的 fmod.dll 并初始化 FMOD Studio 系统。
        /// ★ 先用 LoadLibraryW 把 DLL 按**绝对路径**加载进进程，之后 DllImport("fmod") 才能按模块名找到它
        ///   （.NET 的原生库探测只看 exe 目录和 PATH，不会看插件目录，所以必须先手动加载）。
        /// </summary>
        private static bool EnsureFmod()
        {
            if (_fmodInitTried) return _fmodAvailable;
            _fmodInitTried = true;

            try
            {
                // ① 依次尝试若干候选路径加载 fmod.dll
                List<string> candidates = new List<string>();
                string pdir = PluginDirectory();
                if (!string.IsNullOrEmpty(pdir))
                {
                    candidates.Add(Path.Combine(pdir, "fmod.dll"));
                    // BepInEx 插件目录常见结构：plugins/<Name>/fmod.dll
                    try
                    {
                        DirectoryInfo parent = Directory.GetParent(pdir);
                        if (parent != null)
                        {
                            candidates.Add(Path.Combine(parent.FullName, "fmod.dll"));
                            DirectoryInfo grand = parent.Parent;
                            if (grand != null)
                                candidates.Add(Path.Combine(grand.FullName, "fmod.dll"));
                        }
                    }
                    catch { }
                }
                try { candidates.Add(Path.Combine(Application.dataPath, "fmod.dll")); } catch { }
                // net35 没有 Path.Combine(string,string,string) 重载，用嵌套调用
                try { candidates.Add(Path.Combine(Path.Combine(Application.dataPath, ".."), "fmod.dll")); } catch { }

                string used = null;
                foreach (string c in candidates)
                {
                    try
                    {
                        if (!File.Exists(c)) continue;
                        if (LoadLibraryW(c) != IntPtr.Zero) { used = c; break; }
                    }
                    catch { }
                }

                if (used == null)
                {
                    // 也允许系统 PATH / exe 目录里已有 fmod.dll
                    used = "(依赖系统搜索路径)";
                }

                // ② System_Create —— 版本不符会返回 HEADER_MISMATCH(20)，用候选版本重试
                IntPtr sys = IntPtr.Zero;
                int ret = FMOD_System_Create(out sys, FMOD_VERSION);
                if (ret != FMOD_OK)
                {
                    // 2.01.x / 2.02.x / 2.03.x 各 build 全试一遍（每次调用仅微秒级，代价可忽略）
                    uint[] lines = { 0x00020200u, 0x00020100u, 0x00020300u, 0x00020400u, 0x00020000u, 0x00010000u };
                    for (int li = 0; li < lines.Length && ret != FMOD_OK; li++)
                        for (uint b = 0; b < 256 && ret != FMOD_OK; b++)
                            ret = FMOD_System_Create(out sys, lines[li] | b);
                }
                if (ret != FMOD_OK)
                {
                    _fmodStatus = "fmod.dll 已找到(" + used + ")但 FMOD_System_Create 失败，ret=" + ret;
                    ExplorerCore.LogWarning("[音频/bank] " + _fmodStatus);
                    return false;
                }

                int ir = FMOD_System_Init(sys, 32, 0, IntPtr.Zero);
                if (ir != FMOD_OK)
                {
                    FMOD_System_Release(sys);
                    _fmodStatus = "FMOD_System_Init 失败，ret=" + ir;
                    ExplorerCore.LogWarning("[音频/bank] " + _fmodStatus);
                    return false;
                }

                _fmodSystem = sys;
                _fmodAvailable = true;

                // ★ 布局自检：CREATESOUNDEXINFO 的 cbsize 必须与 FMOD 的头文件一致（x64 应为 224）。
                //   如果这里对不上，FMOD 会按错误的偏移读我们的结构体，症状是 CreateSound 静默失败。
                int exSize = Marshal.SizeOf(typeof(CREATESOUNDEXINFO));
                if (exSize != 224)
                {
                    ExplorerCore.LogWarning("[音频/bank] ★ CREATESOUNDEXINFO 布局异常: sizeof=" + exSize
                        + "（期望 224），CreateSound 可能失败");
                }

                _fmodStatus = "就绪（" + used + "，exinfo=" + exSize + "）";
                ExplorerCore.Log("[音频/bank] fmod.dll 加载成功: " + used + "，FMOD 解码通道就绪（exinfo=" + exSize + "）");
                return true;
            }
            catch (Exception e)
            {
                _fmodStatus = "异常: " + ExMsg(e);
                ExplorerCore.LogWarning("[音频/bank] fmod.dll 初始化失败: " + _fmodStatus
                    + "（音频 bank 直读通道不可用，将回退到 Unity GetData）");
                return false;
            }
        }

        private static string ExMsg(Exception e)
        {
            try
            {
                Exception cur = e;
                int guard = 0;
                while (cur != null && cur.GetType().FullName == "System.Runtime.CompilerServices.RuntimeWrappedException"
                       && guard++ < 4)
                {
                    System.Reflection.PropertyInfo p = cur.GetType().GetProperty("WrappedException");
                    object wrapped = p != null ? p.GetValue(cur, null) : null;
                    if (wrapped is Exception) cur = (Exception)wrapped;
                    else if (wrapped != null) return "原生异常[" + wrapped.GetType().FullName + "] " + wrapped;
                    else break;
                }
                return cur != null ? cur.GetType().Name + ": " + cur.Message : e.Message;
            }
            catch { return e.Message; }
        }

        // ==================================================================
        // 2. FSB5 bank 索引
        // ==================================================================

        private sealed class BankInfo
        {
            public string file;
            public long offset;
            public int size;
            public int channels;      // 解码后声道数
            public int frequency;     // 解码后采样率
            public int frames;        // 解码后帧数（= AudioClip.samples）
        }

        private sealed class FileBankList
        {
            public string file;
            public List<BankInfo> banks = new List<BankInfo>();
        }

        /// <summary>按 .resource 文件缓存的 bank 列表（文件级缓存，跨多次导出复用）。</summary>
        private static readonly Dictionary<string, FileBankList> _bankCache = new Dictionary<string, FileBankList>();
        private static bool _bankScanDone;
        private static int _bankTotal;

        private const int FSB5_HEADER_MIN = 60;

        /// <summary>
        /// 扫描 Application.dataPath 下所有 *.resource，逐段行走切出 FSB5 bank。
        /// </summary>
        private static void EnsureBankScan()
        {
            if (_bankScanDone) return;
            _bankScanDone = true;

            if (!EnsureFmod()) return;

            try
            {
                string dataPath = Application.dataPath;
                if (string.IsNullOrEmpty(dataPath) || !Directory.Exists(dataPath))
                {
                    ExplorerCore.LogWarning("[音频/bank] Application.dataPath 无效: " + dataPath);
                    return;
                }

                string[] files = Directory.GetFiles(dataPath, "*.resource", SearchOption.TopDirectoryOnly);
                int totalBanks = 0;
                foreach (string f in files)
                {
                    FileBankList list = WalkOneResource(f);
                    if (list != null && list.banks.Count > 0)
                    {
                        _bankCache[f] = list;
                        totalBanks += list.banks.Count;
                        ExplorerCore.Log("[音频/bank] " + Path.GetFileName(f) + ": 切出 "
                            + list.banks.Count + " 个 FSB5 bank");
                    }
                }
                _bankTotal = totalBanks;
                if (totalBanks == 0)
                    ExplorerCore.LogWarning("[音频/bank] 未在任何 .resource 中切出 FSB5 bank");
                else
                    ExplorerCore.Log("[音频/bank] 索引完成: " + _bankCache.Count + " 个文件 / "
                        + totalBanks + " 个 bank");
            }
            catch (Exception e)
            {
                ExplorerCore.LogWarning("[音频/bank] 扫描 .resource 失败: " + ExMsg(e));
            }
        }

        /// <summary>
        /// 在单个 .resource 里从偏移 0 开始「行走」切 bank。
        /// 行走而非裸扫 magic：数据内部也会出现 FSB5 字节，只有按公式逐段推进才能保证每步都落在真 bank 头上。
        /// </summary>
        private static FileBankList WalkOneResource(string path)
        {
            FileBankList outList = new FileBankList();
            outList.file = path;

            FileStream fs = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long fileLen = fs.Length;
                if (fileLen < FSB5_HEADER_MIN) return outList;

                long pos = FindFirstFsb5(fs, fileLen);
                if (pos < 0)
                {
                    ExplorerCore.Log("[音频/bank] " + Path.GetFileName(path) + ": 未找到 FSB5 magic，跳过");
                    return outList;
                }

                byte[] hdr = new byte[28];
                int guard = 0;
                while (pos + FSB5_HEADER_MIN <= fileLen && guard++ < 200000)
                {
                    if (fs.Position != pos) fs.Position = pos;
                    if (ReadFull(fs, hdr, 28) != 28) break;
                    if (hdr[0] != (byte)'F' || hdr[1] != (byte)'S' || hdr[2] != (byte)'B' || hdr[3] != (byte)'5')
                        break;   // 走到非 FSB5 处（例如 .resource 尾部混着的 MP4 等），干净收尾

                    int version = BitConverter.ToInt32(hdr, 4);
                    int numSamples = BitConverter.ToInt32(hdr, 8);
                    int sampleHdrSz = BitConverter.ToInt32(hdr, 12);
                    int nameTabSz = BitConverter.ToInt32(hdr, 16);
                    int dataSz = BitConverter.ToInt32(hdr, 20);

                    if (numSamples < 1 || sampleHdrSz < 0 || nameTabSz < 0 || dataSz <= 0) break;

                    // ★ 边界公式（在两个游戏 148 + 409 个真实 bank 上零误差验证）
                    int size = FSB5_HEADER_MIN + sampleHdrSz * numSamples + nameTabSz + dataSz;
                    if (size <= FSB5_HEADER_MIN || pos + size > fileLen) break;

                    BankInfo bi = ReadBankMeta(fs, path, pos, size);
                    if (bi != null) outList.banks.Add(bi);

                    pos += size;
                }
            }
            catch (Exception e)
            {
                ExplorerCore.LogWarning("[音频/bank] 行走 " + Path.GetFileName(path) + " 失败: " + ExMsg(e));
            }
            finally
            {
                if (fs != null) { try { fs.Dispose(); } catch { } }
            }
            return outList;
        }

        private static long FindFirstFsb5(FileStream fs, long fileLen)
        {
            byte[] chunk = new byte[1 << 20];
            long basePos = 0;
            while (basePos < fileLen)
            {
                fs.Position = basePos;
                int n = fs.Read(chunk, 0, chunk.Length);
                if (n <= 0) break;
                for (int i = 0; i + 4 <= n; i++)
                {
                    if (chunk[i] == (byte)'F' && chunk[i + 1] == (byte)'S'
                        && chunk[i + 2] == (byte)'B' && chunk[i + 3] == (byte)'5')
                        return basePos + i;
                }
                basePos += Math.Max(1, n - 3);
            }
            return -1;
        }

        private static int ReadFull(Stream s, byte[] buf, int len)
        {
            int got = 0;
            while (got < len)
            {
                int n = s.Read(buf, got, len - got);
                if (n <= 0) break;
                got += n;
            }
            return got;
        }

        private static byte[] ReadRange(FileStream fs, long offset, int size)
        {
            byte[] buf = new byte[size];
            fs.Position = offset;
            return ReadFull(fs, buf, size) == size ? buf : null;
        }

        /// <summary>
        /// 用 FMOD 读出一个 bank 的解码后元信息（声道/采样率/帧数）。
        /// 只 create_sound + 取元信息 + release，**不做整段解码**，所以很快。
        /// 传入已打开的 FileStream 复用句柄，避免每个 bank 重开一次文件。
        /// </summary>
        private static BankInfo ReadBankMeta(FileStream fs, string path, long offset, int size)
        {
            IntPtr mem = IntPtr.Zero;
            IntPtr snd = IntPtr.Zero;
            IntPtr sub = IntPtr.Zero;
            try
            {
                byte[] blob = ReadRange(fs, offset, size);
                if (blob == null) return null;

                mem = Marshal.AllocHGlobal(size);
                Marshal.Copy(blob, 0, mem, size);

                CREATESOUNDEXINFO ex = new CREATESOUNDEXINFO();
                ex.cbsize = Marshal.SizeOf(typeof(CREATESOUNDEXINFO));
                ex.length = (uint)size;

                int ret = FMOD_System_CreateSound(_fmodSystem, mem, MODE_OPENMEMORY, ref ex, out snd);
                if (ret != FMOD_OK || snd == IntPtr.Zero) return null;

                int nsub;
                if (FMOD_Sound_GetNumSubSounds(snd, out nsub) != FMOD_OK || nsub < 1) return null;
                // ★ FSB 是容器：父 sound 的 format/length 全是空的，必须取 subsound
                if (FMOD_Sound_GetSubSound(snd, 0, out sub) != FMOD_OK || sub == IntPtr.Zero) return null;

                int type, format, ch, bits;
                if (FMOD_Sound_GetFormat(sub, out type, out format, out ch, out bits) != FMOD_OK) return null;
                float freq;
                int prio;
                if (FMOD_Sound_GetDefaults(sub, out freq, out prio) != FMOD_OK) return null;
                uint frames;
                if (FMOD_Sound_GetLength(sub, out frames, TIMEUNIT_PCM) != FMOD_OK) return null;

                if (ch <= 0 || freq <= 0f || frames == 0) return null;

                BankInfo bi = new BankInfo();
                bi.file = path;
                bi.offset = offset;
                bi.size = size;
                bi.channels = ch;
                bi.frequency = (int)(freq + 0.5f);
                bi.frames = (int)frames;
                return bi;
            }
            catch
            {
                return null;
            }
            finally
            {
                // 只释放父 sound：subsound 由父持有，单独释放会双重释放
                if (snd != IntPtr.Zero) { try { FMOD_Sound_Release(snd); } catch { } }
                if (mem != IntPtr.Zero) { try { Marshal.FreeHGlobal(mem); } catch { } }
                // fs 由调用方（WalkOneResource）负责关闭，这里不关
            }
        }

        // ==================================================================
        // 3. Clip ↔ bank 精确对位
        // ==================================================================

        private static readonly Dictionary<int, BankInfo> _clipToBank = new Dictionary<int, BankInfo>();
        private static bool _alignDone;
        private static string _alignNote = "未执行";
        private static int _alignRetries;

        /// <summary>取 clip 的三元组（声道, 采样率, 帧数）—— 用于序列比对。</summary>
        private static bool TryClipTriple(AudioClip clip, out int ch, out int freq, out int frames)
        {
            ch = freq = frames = 0;
            try
            {
                ch = clip.channels;
                freq = clip.frequency;
                frames = clip.samples;
                return ch > 0 && freq > 0 && frames > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 枚举场景内所有 AudioClip（跨 Mono / Unhollower / Il2CppInterop 可用）。
        /// ★ 两个坑都已实机踩过，别再改回去：
        ///   ① IL2CPP 下 `RuntimeHelper.FindObjectsOfTypeAll(typeof(AudioClip))` 会返回**空数组**
        ///      （同一套写法在 AssetExporter 枚举 TextAsset/Texture2D/Mesh 时就已经是
        ///      「先取 Il2CppSystem.Type 再枚举」）；
        ///   ② `all[i] as AudioClip` 对 Il2Cpp 对象**必然失败**，必须 `TryCast<AudioClip>()`。
        /// 两处失败都会得到「0 个 clip」，所以这里把各路线的数量都打出来以便区分。
        /// </summary>
        public static List<AudioClip> EnumerateAllClips()
        {
            UnityEngine.Object[] all = null;
            int viaIl2cpp = -1, viaHelper = -1;
#if CPP
            try
            {
                Il2CppSystem.Type clipType = Il2CppSystem.Type.GetType("UnityEngine.AudioClip, UnityEngine.AudioModule");
                if (clipType != null)
                {
                    all = UnityEngine.Object.FindObjectsOfTypeAll(clipType);
                    viaIl2cpp = all == null ? 0 : all.Length;
                }
                else
                {
                    ExplorerCore.LogWarning("[音频/bank] Il2CppSystem.Type.GetType(\"UnityEngine.AudioClip, UnityEngine.AudioModule\") 返回 null");
                }
            }
            catch (Exception eT)
            {
                ExplorerCore.LogWarning("[音频/bank] AudioClip il2cpp 类型路线异常: " + ExMsg(eT));
            }
#endif
            if (all == null || all.Length == 0)
            {
                try
                {
                    all = RuntimeHelper.FindObjectsOfTypeAll(typeof(AudioClip));
                }
                catch (Exception eF)
                {
                    ExplorerCore.LogWarning("[音频/bank] RuntimeHelper 枚举 AudioClip 异常: " + ExMsg(eF));
                }
                viaHelper = all == null ? 0 : all.Length;
            }

            List<AudioClip> clips = new List<AudioClip>();
            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    UnityEngine.Object o = all[i];
                    if (o == null) continue;
                    AudioClip c = o.TryCast<AudioClip>();     // ★ 不能用 as
                    if (c != null) clips.Add(c);
                }
            }

            ExplorerCore.Log("[音频/bank] AudioClip 枚举: il2cpp路线=" + viaIl2cpp + " RuntimeHelper路线=" + viaHelper
                + " 对象数=" + (all == null ? 0 : all.Length) + " TryCast成功=" + clips.Count);
            return clips;
        }

        /// <summary>
        /// 按名字销毁 AudioClip。
        /// 用途：插件自检会 `AudioClip.Create("UE_AudioSelfTest", ...)` 造一个**运行时** clip，
        /// 它不属于任何 `.resource`。若不销毁，它会混在 `FindObjectsOfTypeAll` 的序列里
        /// （实测排在**最前面**），把「整段对位」模型顶掉。自检结束必须清掉。
        /// </summary>
        public static void DestroyClipsNamed(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                List<AudioClip> all = EnumerateAllClips();
                int killed = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    try
                    {
                        AudioClip c = all[i];
                        if (c != null && c.name == name)
                        {
                            UnityEngine.Object.Destroy(c);
                            killed++;
                        }
                    }
                    catch { }
                }
                if (killed > 0)
                    ExplorerCore.Log("[音频/bank] 已销毁 " + killed + " 个临时 clip（" + name + "），避免污染对位序列");
            }
            catch (Exception e)
            {
                ExplorerCore.LogWarning("[音频/bank] 清理临时 clip 失败: " + ExMsg(e));
            }
        }

        /// <summary>
        /// 建立 clip → bank 映射。
        ///
        /// 核心思想：**.assets 里 AudioClip 的序列化顺序 == 对应 .resource 里 bank 的偏移顺序**，
        /// 所以把「运行时枚举出的 clip 三元组序列」与「各 .resource 文件内按偏移升序的 bank 三元组序列」
        /// 做**整段序列对位**即可精确定位，且逐元素比对本身就是硬校验。
        ///
        /// 为什么不能用三元组直接匹配：同系列分支语音（`Out_P_1..7`、`cum_p_1..9`）时长完全一致，
        /// 实测 148 个 clip 只有 88 个唯一三元组、84 个卷入碰撞 —— 靠三元组只能瞎猜。
        /// </summary>
        private static void EnsureAlignment()
        {
            if (_alignDone) return;
            _alignDone = true;

            try
            {
                List<AudioClip> clips = EnumerateAllClips();
                if (clips.Count == 0) { _alignNote = "场景内没有 AudioClip（或枚举/TryCast 失败）"; return; }

                int[] cch = new int[clips.Count];
                int[] cfreq = new int[clips.Count];
                int[] cfr = new int[clips.Count];
                for (int i = 0; i < clips.Count; i++)
                {
                    int a, b, c;
                    if (!TryClipTriple(clips[i], out a, out b, out c)) { _alignNote = "clip 元信息读取失败"; return; }
                    cch[i] = a; cfreq[i] = b; cfr[i] = c;
                }

                // 用回溯搜索把全部 clip 切成若干段，每段必须与某个 .resource 文件的 bank 序列**整段相同**
                List<FileBankList> lists = new List<FileBankList>(_bankCache.Values);
                bool[] used = new bool[lists.Count];
                int[] assign = new int[clips.Count];       // assign[i] = 所属文件在 lists 中的下标
                for (int i = 0; i < assign.Length; i++) assign[i] = -1;

                bool ok = AssignRec(lists, used, cch, cfreq, cfr, 0, assign);

                // 注：**不需要**再对文件次序做全排列试探。AssignRec 在每个段边界都会尝试
                // 「任意未使用文件」，所以文件之间的串接次序本来就是自由的（离线模拟确认：
                // 把 7 个文件的段按随机次序串接，仍然第 1 次 AssignRec 就命中）。
                // 真正会导致失败的是「只加载了某文件的**部分** clip」，或序列里混进了外来 clip。

                // ★ v16aw 模型 ②：整段块查找。
                // 实机（HTRHGF，2026-09-11 第 2 次）日志已证：枚举顺序**等于**资产序列化顺序，
                // 只是序列最前面多混了一个不属于任何 .resource 的运行时 clip
                // （插件自检的 `UE_AudioSelfTest`，(1,44100,44100)），于是模型 ① 在 clip[0] 就失败，
                // 白白退化成三元组兜底、48/149 个 clip 因歧义得不到映射（含 after_6）。
                // 块查找对「序列里有外来 clip」天然免疫：它只在序列里**搜索**每个文件 bank 序列的
                // 连续整段出现位置，要求位置唯一且各文件的块不重叠，块以外的 clip 一概忽略。
                if (!ok)
                {
                    for (int i = 0; i < assign.Length; i++) assign[i] = -1;
                    ok = TryBlockMatch(clips, cch, cfreq, cfr, lists, assign);
                }

                if (ok)
                {
                    int mapped = 0;
                    for (int i = 0; i < clips.Count; i++)
                    {
                        // 块模型下会有 assign[i] == -1 的「外来 clip」，必须跳过（否则 lists[-1] 崩）
                        if (assign[i] < 0 || assign[i] >= lists.Count) continue;
                        FileBankList l = lists[assign[i]];
                        int idx = IndexOfClipInFile(l, clips, i, assign, lists, cch, cfreq, cfr);
                        if (idx >= 0 && idx < l.banks.Count)
                        {
                            _clipToBank[clips[i].GetInstanceID()] = l.banks[idx];
                            mapped++;
                        }
                    }
                    if (_alignNote == null || _alignNote.IndexOf("块对位") < 0)
                    {
                        _alignNote = "序列对位成功（" + mapped + " 个 clip 精确落到 bank）";
                        ExplorerCore.Log("[音频/bank] " + _alignNote);
                    }
                }
                else
                {
                    _alignNote = "所有对位模型均失败，退回「唯一三元组」匹配";
                    ExplorerCore.LogWarning("[音频/bank] " + _alignNote
                        + " —— 运行时 clip 序列既不是若干 .resource 整段 bank 序列的**串接**，"
                        + "也搜不到任何一个文件的**连续整段**（典型原因：只加载了某文件的部分 clip，"
                        + "但这些 clip 在枚举序列里仍是不连续的），"
                        + "只有三元组在全局唯一的 clip 才能可靠导出");
                    LogAlignDiag(clips, cch, cfreq, cfr, lists);
                    FallbackUniqueTripleMatch(clips, cch, cfreq, cfr);
                }
            }
            catch (Exception e)
            {
                _alignNote = "对位异常: " + ExMsg(e);
                ExplorerCore.LogWarning("[音频/bank] " + _alignNote);
            }
        }

        /// <summary>
        /// 对位失败时的诊断：把「运行时枚举出的 clip 序列」与「各文件 bank 序列」的关系摊开，
        /// 一次实机就能判定到底属于哪种模型（整段串接 / 子序列 / 顺序无关）。
        /// </summary>
        private static void LogAlignDiag(List<AudioClip> clips, int[] cch, int[] cfreq, int[] cfr,
            List<FileBankList> lists)
        {
            try
            {
                string s = "[音频/bank] 诊断·运行时 clip 序列(前10): ";
                for (int i = 0; i < clips.Count && i < 10; i++)
                {
                    if (i > 0) s += ", ";
                    s += clips[i].name + "(" + cch[i] + "," + cfreq[i] + "," + cfr[i] + ")";
                }
                ExplorerCore.Log(s);

                foreach (FileBankList l in lists)
                {
                    if (l.banks == null || l.banks.Count == 0) continue;
                    // 最长公共前缀：clip 序列与该文件 bank 序列从头逐元素相同的个数
                    int lcp = 0;
                    while (lcp < l.banks.Count && lcp < clips.Count
                           && l.banks[lcp].channels == cch[lcp] && l.banks[lcp].frequency == cfreq[lcp]
                           && l.banks[lcp].frames == cfr[lcp]) lcp++;

                    // 贪心子序列：保持两边各自的顺序，最多能配上多少个 clip。
                    // ★ 匹配不上的 clip 要**跳过并继续**，不能中止 —— 序列里可能混着不属于任何
                    //   .resource 的运行时 clip（如插件自检 `UE_AudioSelfTest` 排在首位），
                    //   一旦「首个匹配不上就 return 0」会让这个诊断指标彻底失真。
                    int bi = 0, matched = 0;
                    for (int ci = 0; ci < clips.Count; ci++)
                    {
                        int scan = bi;
                        while (scan < l.banks.Count
                               && !(l.banks[scan].channels == cch[ci] && l.banks[scan].frequency == cfreq[ci]
                                    && l.banks[scan].frames == cfr[ci])) scan++;
                        if (scan < l.banks.Count) { matched++; bi = scan + 1; }
                    }

                    // clip 序列的「每段都等于该文件 bank 整段」的段数（0 = 完全不是整段串接）
                    int segs = 0, p = 0;
                    while (p < clips.Count)
                    {
                        int q = p;
                        while (q < clips.Count && q - p < l.banks.Count
                               && l.banks[q - p].channels == cch[q] && l.banks[q - p].frequency == cfreq[q]
                               && l.banks[q - p].frames == cfr[q]) q++;
                        if (q == p) break;
                        if (q - p == l.banks.Count) segs++;
                        p = q;
                    }

                    ExplorerCore.Log("[音频/bank] 诊断·" + Path.GetFileName(l.file)
                        + ": bank=" + l.banks.Count + " / clip=" + clips.Count
                        + " | 最长公共前缀=" + lcp
                        + " | 子序列可匹配=" + matched
                        + " | 整段可切=" + segs);
                }
            }
            catch (Exception e)
            {
                ExplorerCore.LogWarning("[音频/bank] 诊断输出失败: " + ExMsg(e));
            }
        }

        /// <summary>回溯：把 clip 序列按段分配给各个 .resource 文件（每段必须与该文件 bank 序列整段相同）。</summary>
        private static bool AssignRec(List<FileBankList> lists, bool[] used,
            int[] cch, int[] cfreq, int[] cfr, int clipStart, int[] assign)
        {
            if (clipStart >= cch.Length)
            {
                // 所有已加载的 clip 都分配完；文件可以被部分使用（没加载的文件就不参与）
                return true;
            }

            for (int li = 0; li < lists.Count; li++)
            {
                if (used[li]) continue;
                List<BankInfo> banks = lists[li].banks;
                if (banks.Count == 0) continue;
                if (clipStart + banks.Count > cch.Length) continue;

                bool match = true;
                for (int k = 0; k < banks.Count; k++)
                {
                    BankInfo b = banks[k];
                    if (b.channels != cch[clipStart + k] || b.frequency != cfreq[clipStart + k]
                        || b.frames != cfr[clipStart + k]) { match = false; break; }
                }
                if (!match) continue;

                int old = assign[clipStart];
                used[li] = true;
                for (int k = 0; k < banks.Count; k++) assign[clipStart + k] = li;

                if (AssignRec(lists, used, cch, cfreq, cfr, clipStart + banks.Count, assign))
                    return true;

                used[li] = false;
                for (int k = 0; k < banks.Count; k++) assign[clipStart + k] = old;
            }
            return false;
        }

        /// <summary>
        /// v16aw 对位模型 ②：**整段块查找**。
        ///
        /// 在 clip 序列里搜索每个 `.resource` 的 bank 序列作为**连续整段**出现的位置：
        ///   - 命中位置必须**唯一**（出现 ≥2 次说明有歧义 → 该文件放弃，宁缺勿错）；
        ///   - 各文件找到的块之间**不得重叠**；
        ///   - 块以外的 clip 一概忽略 —— 这正是它比模型 ① 强的地方：序列里混进
        ///     不属于任何 .resource 的运行时 clip（如插件自检 `UE_AudioSelfTest`）也不受影响。
        /// 命中后把 assign[pos + k] 写成该文件下标，后续落表逻辑（IndexOfClipInFile 按 assign
        /// 回溯段起点）可直接复用。
        /// </summary>
        private static bool TryBlockMatch(List<AudioClip> clips, int[] cch, int[] cfreq, int[] cfr,
            List<FileBankList> lists, int[] assign)
        {
            int n = clips.Count;
            bool[] taken = new bool[n];
            int foundFiles = 0, covered = 0;
            string detail = "";

            for (int li = 0; li < lists.Count; li++)
            {
                List<BankInfo> banks = lists[li].banks;
                int m = banks.Count;
                if (m == 0 || m > n) continue;

                int pos = -1, hits = 0;
                for (int p = 0; p + m <= n; p++)
                {
                    bool eq = true;
                    for (int k = 0; k < m; k++)
                    {
                        BankInfo b = banks[k];
                        if (b.channels != cch[p + k] || b.frequency != cfreq[p + k] || b.frames != cfr[p + k])
                        { eq = false; break; }
                    }
                    if (!eq) continue;
                    hits++;
                    if (pos < 0) pos = p;
                    if (hits > 1) break;                 // 已不止一处 → 歧义，放弃该文件
                }
                if (hits != 1) continue;

                bool clash = false;
                for (int k = 0; k < m; k++) if (taken[pos + k]) { clash = true; break; }
                if (clash) continue;

                for (int k = 0; k < m; k++) { assign[pos + k] = li; taken[pos + k] = true; }
                foundFiles++;
                covered += m;
                detail += " " + Path.GetFileName(lists[li].file) + "→clip[" + pos + ".." + (pos + m) + ")";
            }

            if (foundFiles == 0) return false;

            _alignNote = "整段块对位成功（" + foundFiles + " 个文件 / 覆盖 " + covered + " 个 clip）";
            ExplorerCore.Log("[音频/bank] " + _alignNote + ":" + detail);
            return true;
        }

        private static int IndexOfClipInFile(FileBankList l, List<AudioClip> clips, int clipIdx,
            int[] assign, List<FileBankList> lists, int[] cch, int[] cfreq, int[] cfr)
        {
            // 段起点回推
            int start = clipIdx;
            while (start > 0 && assign[start - 1] == assign[clipIdx]) start--;
            return clipIdx - start;
        }

        /// <summary>兜底：只对「三元组在整个 bank 集合里唯一」的 clip 建立映射。</summary>
        /// <summary>
        /// 顺序无关的兜底匹配（三级递进）。实机已证：运行时枚举出的 AudioClip 顺序
        /// **不等于**资产的序列化顺序，且场景往往只加载了某个 .resource 的**部分** clip，
        /// 所以「整段串接」模型经常用不上。这里改用不依赖全局顺序的办法：
        ///
        ///   ① 三元组在**全库**唯一的 clip → 直接确定（这部分与顺序无关，最可靠）。
        ///   ② 把 ① 当成**锚点**，再验证若干「clip 次序假设」下，锚点在同一文件内是否随
        ///      bank 偏移**单调递增**。单调性是很强的条件（k 个锚点乱序恰好单调的概率约 1/k!），
        ///      一旦通过就等价于证明该次序成立 —— 于是把**歧义 clip 的候选 bank 夹逼到
        ///      相邻锚点之间**，只剩一个候选即确定。
        ///   ③ 仍歧义的 → 放弃（宁缺勿错），由上层打「采样全为 0」告警。
        /// </summary>
        private static void FallbackUniqueTripleMatch(List<AudioClip> clips, int[] cch, int[] cfreq, int[] cfr)
        {
            int n = clips.Count;
            List<FileBankList> lists = new List<FileBankList>(_bankCache.Values);

            // 候选表：三元组 → 候选 (文件下标, 文件内 bank 下标)
            Dictionary<string, List<int[]>> cand = new Dictionary<string, List<int[]>>();
            for (int f = 0; f < lists.Count; f++)
            {
                List<BankInfo> bs = lists[f].banks;
                for (int k = 0; k < bs.Count; k++)
                {
                    string key = bs[k].channels + "/" + bs[k].frequency + "/" + bs[k].frames;
                    List<int[]> lst;
                    if (!cand.TryGetValue(key, out lst)) { lst = new List<int[]>(); cand[key] = lst; }
                    lst.Add(new int[] { f, k });
                }
            }

            List<int[]>[] cands = new List<int[]>[n];
            List<int> anchors = new List<int>();
            for (int i = 0; i < n; i++)
            {
                string key = cch[i] + "/" + cfreq[i] + "/" + cfr[i];
                List<int[]> lst;
                cands[i] = cand.TryGetValue(key, out lst) ? lst : new List<int[]>();
                if (cands[i].Count == 1) anchors.Add(i);
            }

            // ---------- ① 全库唯一 → 直接确定 ----------
            int[][] bestChosen = new int[n][];
            for (int a = 0; a < anchors.Count; a++)
                bestChosen[anchors[a]] = cands[anchors[a]][0];
            int bestResolved = anchors.Count;
            string bestHyp = null;

            if (n == 0) { ExplorerCore.Log("[音频/bank] 顺序无关兜底: 无 clip"); return; }

            // ---------- ② 锚点单调性 → 夹逼解歧 ----------
            List<int> base0 = new List<int>();
            for (int i = 0; i < n; i++) base0.Add(i);

            string[] hypNames = { "枚举顺序", "实例ID升序", "实例ID降序", "名称序" };
            List<int>[] hyps = new List<int>[4];
            hyps[0] = new List<int>(base0);
            hyps[1] = new List<int>(base0);
            hyps[1].Sort((a, b) => clips[a].GetInstanceID().CompareTo(clips[b].GetInstanceID()));
            hyps[2] = new List<int>(base0);
            hyps[2].Sort((a, b) => clips[b].GetInstanceID().CompareTo(clips[a].GetInstanceID()));
            hyps[3] = new List<int>(base0);
            hyps[3].Sort((a, b) => string.CompareOrdinal(clips[a].name ?? "", clips[b].name ?? ""));

            for (int h = 0; h < hyps.Length && anchors.Count > 0; h++)
            {
                List<int> ord = hyps[h];
                int[] rank = new int[n];
                for (int r = 0; r < ord.Count; r++) rank[ord[r]] = r;

                // 锚点在同一文件内的 bank 下标必须严格递增
                bool mono = true;
                Dictionary<int, int> lastPerFile = new Dictionary<int, int>();
                for (int r = 0; r < ord.Count && mono; r++)
                {
                    int i = ord[r];
                    if (cands[i].Count != 1) continue;
                    int f = cands[i][0][0], k = cands[i][0][1];
                    int last;
                    if (lastPerFile.TryGetValue(f, out last) && k <= last) mono = false;
                    else lastPerFile[f] = k;
                }
                if (!mono) continue;

                int[][] chosen = new int[n][];
                for (int a = 0; a < anchors.Count; a++) chosen[anchors[a]] = cands[anchors[a]][0];
                int resolved = anchors.Count;

                for (int i = 0; i < n; i++)
                {
                    if (cands[i].Count <= 1) continue;
                    int r = rank[i];
                    int[] pick = null;
                    int pickCount = 0;
                    for (int ci = 0; ci < cands[i].Count; ci++)
                    {
                        int[] c = cands[i][ci];
                        int f = c[0], k = c[1];
                        bool okC = true;
                        // rank 更小的、同文件的最近锚点
                        for (int rr = r - 1; rr >= 0; rr--)
                        {
                            int j = ord[rr];
                            if (chosen[j] == null) continue;
                            if (chosen[j][0] != f) continue;
                            if (chosen[j][1] >= k) okC = false;
                            break;
                        }
                        // rank 更大的、同文件的最近锚点
                        for (int rr = r + 1; rr < ord.Count && okC; rr++)
                        {
                            int j = ord[rr];
                            if (chosen[j] == null) continue;
                            if (chosen[j][0] != f) continue;
                            if (chosen[j][1] <= k) okC = false;
                            break;
                        }
                        if (okC) { pick = c; pickCount++; }
                    }
                    if (pickCount == 1) { chosen[i] = pick; resolved++; }
                }

                if (resolved > bestResolved)
                {
                    bestResolved = resolved;
                    bestChosen = chosen;
                    bestHyp = hypNames[h];
                }
                if (resolved == n) break;
            }

            // ---------- 落表 ----------
            int hit = 0;
            for (int i = 0; i < n; i++)
            {
                int[] c = bestChosen[i];
                if (c == null) continue;
                List<BankInfo> bs = lists[c[0]].banks;
                if (c[1] < 0 || c[1] >= bs.Count) continue;
                _clipToBank[clips[i].GetInstanceID()] = bs[c[1]];
                hit++;
            }

            ExplorerCore.Log("[音频/bank] 顺序无关兜底: 命中 " + hit + " / " + n
                + "（全库唯一锚点 " + anchors.Count
                + "，次序假设=" + (bestHyp ?? "未通过单调性检验")
                + (hit < n ? "，仍有 " + (n - hit) + " 个 clip 三元组歧义 → 放弃，宁缺勿错" : "") + "）");
        }

        // ==================================================================
        // 4. 解码 + 对外入口
        // ==================================================================

        private static BankInfo FindBankFor(AudioClip clip)
        {
            EnsureBankScan();
            if (_bankCache.Count == 0) return null;
            EnsureAlignment();

            BankInfo b;
            if (_clipToBank.TryGetValue(clip.GetInstanceID(), out b)) return b;

            // 对位是一次性的，但游戏换场景后会加载**新**的 AudioClip。
            // 没命中就重算一次对位（限次，避免真的对不上时反复扫描）。
            if (_alignRetries < 8)
            {
                _alignRetries++;
                _alignDone = false;
                _clipToBank.Clear();
                EnsureAlignment();
                if (_clipToBank.TryGetValue(clip.GetInstanceID(), out b))
                {
                    _alignRetries = 0;   // 重算成功 → 说明确实换了场景，允许下次继续重算
                    return b;
                }
            }
            return null;
        }

        /// <summary>
        /// v16au：从游戏自己的 .resource 里读出该 AudioClip 的 PCM。
        /// 成功返回 true 并给出与 `clip.samples * clip.channels` 等长的 float[]（-1..1），
        /// 这样上游的 WAV 写出逻辑可以完全复用。
        /// </summary>
        public static bool TryReadFromResourceBank(AudioClip clip, out float[] samples, out string why)
        {
            samples = null;
            why = null;

            try
            {
                if (clip == null) { why = "clip 为空"; return false; }
                if (!EnsureFmod()) { why = "fmod.dll 不可用（" + _fmodStatus + "）"; return false; }

                BankInfo bank = FindBankFor(clip);
                if (bank == null) { why = "未能在 .resource 中定位到对应 FSB5 bank（" + _alignNote + "）"; return false; }

                // ★ v16ax 元信息硬校验 —— bank 通道被提到 `GetData` **之前**后，这是必须的安全阀。
                //   对位一旦出错，代价就从「导出静音」升级为「静默导出**别的音频**」，后者致命得多。
                //   所以每个 bank 交付前必须自证身份：三个元信息与 clip **精确相等**。
                //   这三个量和对位算法用的是同一组量（`BankInfo.frames` 取自 `TIMEUNIT_PCM`，
                //   定义上就等于 `AudioClip.samples`），离线已确认 10/10 精确吻合。
                int mcch = clip.channels, mcfreq = clip.frequency, mcfr = clip.samples;
                if (bank.channels != mcch || bank.frequency != mcfreq || bank.frames != mcfr)
                {
                    why = "bank 元信息与 clip 不符（ch " + bank.channels + "/" + mcch
                        + " freq " + bank.frequency + "/" + mcfreq
                        + " frames " + bank.frames + "/" + mcfr + "）→ 判为对位错误，拒用";
                    ExplorerCore.LogWarning("[音频/bank] " + clip.name + " " + why);
                    return false;
                }

                float[] pcm;
                string decWhy;
                if (!DecodeBank(bank, clip, out pcm, out decWhy)) { why = decWhy; return false; }

                long needLen = (long)mcfr * mcch;      // 交错声道总样点数
                if (pcm.Length < needLen)
                {
                    why = "bank 解码长度不足 " + pcm.Length + " < " + needLen + "（对位可疑，拒用）";
                    ExplorerCore.LogWarning("[音频/bank] " + clip.name + " " + why);
                    return false;
                }

                samples = pcm;
                ExplorerCore.Log("[音频/bank] " + clip.name + " 命中 bank: " + Path.GetFileName(bank.file)
                    + " offset=" + bank.offset + " size=" + bank.size
                    + " | 解出 ch=" + bank.channels + " freq=" + bank.frequency
                    + " frames=" + bank.frames + " 峰值=" + PeakInt16(pcm));
                return true;
            }
            catch (Exception e)
            {
                why = ExMsg(e);
                return false;
            }
        }

        private static int PeakInt16(float[] buf)
        {
            int peak = 0;
            if (buf == null) return -1;
            for (int i = 0; i < buf.Length; i++)
            {
                int v = (int)(buf[i] * 32767f);
                if (v < 0) v = -v;
                if (v > peak) peak = v;
            }
            return peak;
        }

        /// <summary>把一个 bank 完整解成 float[]（-1..1，交错声道）。</summary>
        private static bool DecodeBank(BankInfo bank, AudioClip clip, out float[] samples, out string why)
        {
            samples = null;
            why = null;

            FileStream fs = null;
            IntPtr mem = IntPtr.Zero;
            IntPtr snd = IntPtr.Zero;
            IntPtr sub = IntPtr.Zero;
            IntPtr p1 = IntPtr.Zero, p2 = IntPtr.Zero;
            uint n1 = 0, n2 = 0;
            bool locked = false;

            try
            {
                fs = new FileStream(bank.file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] blob = ReadRange(fs, bank.offset, bank.size);
                if (blob == null) { why = "读取 bank 字节失败"; return false; }

                mem = Marshal.AllocHGlobal(bank.size);
                Marshal.Copy(blob, 0, mem, bank.size);

                CREATESOUNDEXINFO ex = new CREATESOUNDEXINFO();
                ex.cbsize = Marshal.SizeOf(typeof(CREATESOUNDEXINFO));
                ex.length = (uint)bank.size;

                int ret = FMOD_System_CreateSound(_fmodSystem, mem, MODE_OPENMEMORY, ref ex, out snd);
                if (ret != FMOD_OK || snd == IntPtr.Zero) { why = "CreateSound 失败 ret=" + ret; return false; }

                int nsub;
                if (FMOD_Sound_GetNumSubSounds(snd, out nsub) != FMOD_OK || nsub < 1) { why = "无 subsound"; return false; }
                if (FMOD_Sound_GetSubSound(snd, 0, out sub) != FMOD_OK || sub == IntPtr.Zero)
                { why = "GetSubSound 失败"; return false; }

                int type, format, ch, bits;
                if (FMOD_Sound_GetFormat(sub, out type, out format, out ch, out bits) != FMOD_OK)
                { why = "GetFormat 失败"; return false; }

                uint totalBytes;
                if (FMOD_Sound_GetLength(sub, out totalBytes, TIMEUNIT_PCMBYTES) != FMOD_OK || totalBytes == 0)
                { why = "GetLength(PCMBYTES)=0"; return false; }

                int bytesPerSample = bits > 0 ? bits / 8 : 0;
                if (format == SOUNDFORMAT_PCMFLOAT) bytesPerSample = 4;
                if (bytesPerSample <= 0) { why = "未知位深 bits=" + bits; return false; }

                if (FMOD_Sound_Lock(sub, 0, totalBytes, out p1, out p2, out n1, out n2) != FMOD_OK)
                { why = "Sound_Lock 失败（该 bank 不可 lock）"; return false; }
                locked = true;

                long byteLen = (long)n1 + (long)n2;
                if (byteLen <= 0) { why = "lock 返回长度 0"; return false; }

                int count = (int)(byteLen / bytesPerSample);
                float[] outBuf = new float[count];

                if (format == SOUNDFORMAT_PCMFLOAT)
                {
                    float[] tmp = new float[count];
                    Marshal.Copy(p1, tmp, 0, (int)(n1 / 4));
                    if (n2 > 0) Marshal.Copy(p2, tmp, (int)(n1 / 4), (int)(n2 / 4));
                    Array.Copy(tmp, outBuf, count);
                }
                else if (format == SOUNDFORMAT_PCM16)
                {
                    short[] tmp = new short[count];
                    Marshal.Copy(p1, tmp, 0, (int)(n1 / 2));
                    if (n2 > 0) Marshal.Copy(p2, tmp, (int)(n1 / 2), (int)(n2 / 2));
                    for (int i = 0; i < count; i++) outBuf[i] = tmp[i] / 32768f;
                }
                else if (format == SOUNDFORMAT_PCM8)
                {
                    byte[] tmp = new byte[count];
                    Marshal.Copy(p1, tmp, 0, (int)n1);
                    if (n2 > 0) Marshal.Copy(p2, tmp, (int)n1, (int)n2);
                    for (int i = 0; i < count; i++) outBuf[i] = (tmp[i] - 128) / 128f;
                }
                else if (format == SOUNDFORMAT_PCM24)
                {
                    byte[] tmp = new byte[count * 3];
                    Marshal.Copy(p1, tmp, 0, (int)n1);
                    if (n2 > 0) Marshal.Copy(p2, tmp, (int)n1, (int)n2);
                    for (int i = 0; i < count; i++)
                    {
                        int v = tmp[i * 3] | (tmp[i * 3 + 1] << 8) | ((sbyte)tmp[i * 3 + 2] << 16);
                        outBuf[i] = v / 8388608f;
                    }
                }
                else if (format == SOUNDFORMAT_PCM32)
                {
                    int[] tmp = new int[count];
                    Marshal.Copy(p1, tmp, 0, (int)(n1 / 4));
                    if (n2 > 0) Marshal.Copy(p2, tmp, (int)(n1 / 4), (int)(n2 / 4));
                    for (int i = 0; i < count; i++) outBuf[i] = tmp[i] / 2147483648f;
                }
                else
                {
                    why = "不支持的 SOUND_FORMAT=" + format;
                    return false;
                }

                FMOD_Sound_Unlock(sub, p1, p2, n1, n2);
                locked = false;

                samples = outBuf;
                return true;
            }
            catch (Exception e)
            {
                why = ExMsg(e);
                return false;
            }
            finally
            {
                if (locked) { try { FMOD_Sound_Unlock(sub, p1, p2, n1, n2); } catch { } }
                if (sub != IntPtr.Zero) { try { FMOD_Sound_Release(sub); } catch { } }
                if (snd != IntPtr.Zero) { try { FMOD_Sound_Release(snd); } catch { } }
                if (mem != IntPtr.Zero) { try { Marshal.FreeHGlobal(mem); } catch { } }
                if (fs != null) { try { fs.Dispose(); } catch { } }
            }
        }

        /// <summary>供日志展示用的一行状态。</summary>
        public static string DescribeStatus()
        {
            return "fmod=" + _fmodStatus + ", bank 文件=" + _bankCache.Count + ", bank 总数=" + _bankTotal
                + ", 对位=" + _alignNote;
        }
    }
}
