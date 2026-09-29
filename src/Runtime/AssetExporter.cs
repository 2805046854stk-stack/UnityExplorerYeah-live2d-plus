using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityExplorer.Config;
using UnityEngine;

namespace UnityExplorer.Runtime
{
    /// <summary>
    /// 资源导出工具：从 GameObject 提取并导出纹理、模型、音频、材质、Live2D（Cubism）、Spine 资源。
    ///
    /// 实现依据（来自官方 SDK 源码，2026-09 核对）：
    /// - Live2D Cubism SDK 4/5 (Live2D/CubismUnityComponents develop 分支):
    ///     CubismModel._moc  : CubismMoc（SerializeField）
    ///     CubismMoc._bytes  : byte[] —— moc3 文件原始字节（SerializeField，不是 TextAsset！）
    ///     CubismRenderer._mainTexture : Texture2D（贴图经 MaterialPropertyBlock 应用到 MeshRenderer）
    /// - Spine-Unity 4.x (EsotericSoftware/spine-runtimes):
    ///     SkeletonRenderer.skeletonDataAsset : SkeletonDataAsset
    ///     SkeletonDataAsset.skeletonJSON : TextAsset（.json 或 .skel.bytes）
    ///     SkeletonDataAsset.atlasAssets : AtlasAssetBase[]
    ///     SpineAtlasAsset.atlasFile : TextAsset（.atlas.txt）
    ///     SpineAtlasAsset.materials : Material[]（贴图在材质 mainTexture 上）
    /// </summary>
    public static class AssetExporter
    {
        /// <summary>
        /// 可导出资源项
        /// </summary>
        public class ExportableItem
        {
            public string Name;
            public string Type;
            public UnityEngine.Object Asset;
            public System.Action<string> ExportAction;
            public bool IsChecked = true; // UI 勾选状态，默认导出
            /// <summary>
            /// v16ai：显示用分组名（如具体模型名 / 子目录名）。仅用于资源列表区分
            /// 不同模型的同名文件，不参与磁盘文件名拼接。
            /// </summary>
            public string Group;
        }

        /// <summary>
        /// 游戏维度类型
        /// </summary>
        public enum GameDimension
        {
            Unknown,
            Game2D,
            Game3D,
            Mixed
        }

        /// <summary>
        /// 资产类型（用于"各自只导出各自对应文件"的过滤）
        /// </summary>
        public enum AssetKind
        {
            Unknown,    // 未知（不过滤）
            Model3D,    // 3D 模型：只导 Mesh(.fbx)/贴图/音频
            Live2D,     // Live2D Cubism：只导 moc3/moc + 贴图 + model3.json + motion3.json
            Spine,      // Spine：只导 .json/.skel + 贴图 + .atlas
            Sprite2D    // 普通 2D：只导贴图
        }

        // 收集状态（每次导出收集时重置）
        private static readonly HashSet<string> exportedNames = new();
        private static readonly HashSet<int> collectedTexIds = new();
        private static readonly HashSet<int> processedCubismModels = new();
        private static readonly HashSet<int> processedSpineComps = new();
        // v16u：Spine 贴图已按 atlas.txt 尺寸加入重采样导出项的贴图实例ID（防止多个 atlasAsset 重复触发）
        private static readonly HashSet<int> spineResizeAddedIds = new();
        private static readonly HashSet<string> dumpedComponentTypes = new();
        private static int textAssetScanCount = 0;
        private static bool byteArrayDiagLogged = false;

        private const int MaxRecursionDepth = 64;
        private const int MaxObjectsToScan = 3000;
        private const int MaxTextAssetFieldScans = 300;
        private static int scannedObjectCount = 0;

        #region 资源缓存（启动/首次导出时全局扫描）

        /// <summary>
        /// 资源缓存：游戏运行时扫描一次，把关键资源暂存到托管内存，
        /// 导出时直接从缓存读取（兜底用）。
        /// </summary>
        public static class AssetCache
        {
            public static Dictionary<string, byte[]> TextAssetBytes = new();
            public static List<Texture2D> Textures = new();
            public static List<Mesh> Meshes = new();
            public static bool Scanned = false;
            public static int TextAssetTotal = 0;
            public static int TextAssetReadOK = 0;
            public static int TextAssetReadFail = 0;
            public static int TextureTotal = 0;
            public static int MeshTotal = 0;
        }

        /// <summary>
        /// 扫描并缓存所有 TextAsset / Texture2D / Mesh 到托管内存。
        /// </summary>
        public static void ScanAndCacheAssets()
        {
            try
            {
                AssetCache.TextAssetReadOK = 0;
                AssetCache.TextAssetReadFail = 0;
                AssetCache.TextAssetTotal = 0;
                AssetCache.TextureTotal = 0;
                AssetCache.MeshTotal = 0;
                // 重复扫描时按实例ID去重，避免列表无限翻倍（129→258→...）
                AssetCache.Textures = new List<Texture2D>(DedupeByInstanceId(AssetCache.Textures));
                AssetCache.Meshes = new List<Mesh>(DedupeByInstanceId(AssetCache.Meshes));

                ExplorerCore.Log("[缓存] 开始扫描场景资源...");

                // ---- TextAsset：读完整字节暂存（bytes 失败时用 text 兜底）----
                try
                {
                    UnityEngine.Object[] all = null;
#if CPP
                    try
                    {
                        Il2CppSystem.Type il2cppType = Il2CppSystem.Type.GetType("UnityEngine.TextAsset, UnityEngine.CoreModule");
                        if (il2cppType != null)
                            all = UnityEngine.Object.FindObjectsOfTypeAll(il2cppType);
                    }
                    catch (System.Exception exT)
                    {
                        ExplorerCore.LogWarning($"[缓存] TextAsset 类型获取失败: {exT.Message}");
                        all = null;
                    }
#else
                    all = UnityEngine.Object.FindObjectsOfTypeAll(typeof(TextAsset));
#endif
                    AssetCache.TextAssetTotal = all != null ? all.Length : 0;
                    if (all != null && all.Length > 0)
                    {
                        int asFail = 0, bytesFail = 0;
                        foreach (UnityEngine.Object o in all)
                        {
                            try
                            {
                                // IL2CPP Interop 下必须用 TryCast（as 对 Il2Cpp 对象失败）
                                TextAsset ta = o.TryCast<TextAsset>();
                                if (ta == null)
                                {
                                    asFail++;
                                    continue;
                                }
                                byte[] bytes = ReadTextAssetFull(ta);
                                if (bytes != null && bytes.Length > 0)
                                {
                                    string name = string.IsNullOrEmpty(ta.name) ? "textasset_" + ta.GetInstanceID() : ta.name;
                                    if (!AssetCache.TextAssetBytes.ContainsKey(name))
                                        AssetCache.TextAssetBytes.Add(name, bytes);
                                    AssetCache.TextAssetReadOK++;
                                }
                                else
                                {
                                    bytesFail++;
                                }
                            }
                            catch { bytesFail++; }
                        }
                        AssetCache.TextAssetReadFail = asFail + bytesFail;
                        ExplorerCore.Log($"[缓存] TextAsset: as转换失败 {asFail}, 数据读取失败 {bytesFail}, 成功 {AssetCache.TextAssetReadOK}/{AssetCache.TextAssetTotal}");
                    }
                    else if (all == null)
                    {
                        ExplorerCore.Log("[缓存] TextAsset 全局扫描返回空");
                    }
                }
                catch (System.Exception ex)
                {
                    ExplorerCore.LogWarning($"[缓存] TextAsset 扫描失败: {ex.Message}");
                }

                // ---- Texture2D：暂存引用 ----
                try
                {
                    UnityEngine.Object[] all = null;
#if CPP
                    try
                    {
                        Il2CppSystem.Type il2cppType = Il2CppSystem.Type.GetType("UnityEngine.Texture2D, UnityEngine.CoreModule");
                        if (il2cppType != null)
                            all = UnityEngine.Object.FindObjectsOfTypeAll(il2cppType);
                    }
                    catch { all = null; }
#else
                    all = UnityEngine.Object.FindObjectsOfTypeAll(typeof(Texture2D));
#endif
                    AssetCache.TextureTotal = all != null ? all.Length : 0;
                    int asFail = 0;
                    if (all != null)
                    {
                        foreach (UnityEngine.Object o in all)
                        {
                            try
                            {
                                Texture2D tex = o.TryCast<Texture2D>();
                                if (tex == null)
                                {
                                    asFail++;
                                    continue;
                                }
                                if (tex.width > 4 && tex.height > 4)
                                    AssetCache.Textures.Add(tex);
                            }
                            catch { }
                        }
                    }
                    ExplorerCore.Log($"[缓存] Texture2D 共 {AssetCache.TextureTotal} 个, as转换失败 {asFail}, 缓存有效贴图 {AssetCache.Textures.Count} 个");
                }
                catch (System.Exception ex)
                {
                    ExplorerCore.LogWarning($"[缓存] Texture2D 扫描失败: {ex.Message}");
                }

                // ---- Mesh：暂存引用 ----
                try
                {
                    UnityEngine.Object[] all = null;
#if CPP
                    try
                    {
                        Il2CppSystem.Type il2cppType = Il2CppSystem.Type.GetType("UnityEngine.Mesh, UnityEngine.CoreModule");
                        if (il2cppType != null)
                            all = UnityEngine.Object.FindObjectsOfTypeAll(il2cppType);
                    }
                    catch { all = null; }
#else
                    all = UnityEngine.Object.FindObjectsOfTypeAll(typeof(Mesh));
#endif
                    AssetCache.MeshTotal = all != null ? all.Length : 0;
                    int asFail = 0;
                    if (all != null)
                    {
                        foreach (UnityEngine.Object o in all)
                        {
                            try
                            {
                                Mesh m = o.TryCast<Mesh>();
                                if (m == null)
                                {
                                    asFail++;
                                    continue;
                                }
                                if (m.vertexCount > 0)
                                    AssetCache.Meshes.Add(m);
                            }
                            catch { }
                        }
                    }
                    ExplorerCore.Log($"[缓存] Mesh 共 {AssetCache.MeshTotal} 个, as转换失败 {asFail}, 缓存有效 {AssetCache.Meshes.Count} 个");
                }
                catch (System.Exception ex)
                {
                    ExplorerCore.LogWarning($"[缓存] Mesh 扫描失败: {ex.Message}");
                }

                AssetCache.Scanned = true;
                ExplorerCore.Log("[缓存] 资源扫描完成，可以在导出列表中使用缓存数据");
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"[缓存] 扫描失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 完整读取 TextAsset 数据：优先 bytes，失败时用 text 转字节。
        /// </summary>
        private static byte[] ReadTextAssetFull(TextAsset ta)
        {
            if (ta == null)
                return null;

#if CPP
            // 重要：IL2CPP 下禁止强类型调用 ta.bytes / ta.text ——
            // 若底层方法被剥离，会在 JIT 阶段抛 Method not found 且无法捕获，
            // 导致整个方法体不执行（此前 1017 个全部失败且诊断不打印的根因）。
            return ReadTextAssetFullLateBound(ta);
#else
            try
            {
                byte[] b = ta.bytes;
                if (b != null && b.Length > 0) return b;
            }
            catch { }
            try
            {
                string t = ta.text;
                if (!string.IsNullOrEmpty(t)) return Encoding.UTF8.GetBytes(t);
            }
            catch { }
            return ReadTextAssetFullLateBound(ta);
#endif
        }

        /// <summary>
        /// 晚绑定读取 TextAsset 内容：get_bytes/get_text 反射调用 → 枚举全部字段（byte[] 优先 → script/text/data string）。
        /// </summary>
        private static byte[] ReadTextAssetFullLateBound(TextAsset ta)
        {
            try
            {
#if CPP
                Il2CppSystem.Object iobj = ta.TryCast<Il2CppSystem.Object>();
                if (iobj == null) return null;
                Il2CppSystem.Type taType = iobj.GetIl2CppType();
                if (taType == null) return null;
                var flags = Il2CppSystem.Reflection.BindingFlags.Instance
                    | Il2CppSystem.Reflection.BindingFlags.Public
                    | Il2CppSystem.Reflection.BindingFlags.NonPublic;

                // 1) 晚绑定调用 get_bytes / get_text（剥离时异常可捕获，不会炸整个方法）
                try
                {
                    var m = taType.GetMethod("get_bytes", flags);
                    if (m != null)
                    {
                        object r = m.Invoke(iobj, null);
                        byte[] b = TryConvertToByteArray(r);
                        if (b != null && b.Length > 0)
                            return b;
                    }
                }
                catch { }
                try
                {
                    var m = taType.GetMethod("get_text", flags);
                    if (m != null)
                    {
                        object r = m.Invoke(iobj, null);
                        string s = Il2CppObjToString(r);
                        if (!string.IsNullOrEmpty(s))
                            return Encoding.UTF8.GetBytes(s);
                    }
                }
                catch { }

                // 2) 枚举全部字段
                var fields = taType.GetFields(flags);

                // 首次失败时输出一次诊断：字段名 + 值类型/长度摘要，便于定位
                if (!textAssetDiagLogged)
                {
                    textAssetDiagLogged = true;
                    StringBuilder sb = new StringBuilder();
                    if (fields != null)
                    {
                        foreach (var f in fields)
                        {
                            try
                            {
                                object v = null;
                                try { v = f.GetValue(iobj); } catch { }
                                string vdesc = "null";
                                if (v != null)
                                {
                                    byte[] ba = null;
                                    try { ba = TryConvertToByteArray(v); } catch { }
                                    if (ba != null)
                                        vdesc = $"byte[{ba.Length}]";
                                    else
                                    {
                                        string s2 = Il2CppObjToString(v);
                                        if (!string.IsNullOrEmpty(s2))
                                            vdesc = $"str({s2.Length}) \"{s2.Substring(0, System.Math.Min(24, s2.Length))}\"";
                                        else
                                            vdesc = GetObjectTypeName(v);
                                    }
                                }
                                sb.Append(f.Name).Append('=').Append(vdesc).Append(", ");
                            }
                            catch { }
                        }
                    }
                    ExplorerCore.LogWarning($"[诊断] TextAsset '{ta.name}' 属性直读失败，字段清单: {sb}");
                }

                if (fields != null)
                {
                    // 第一遍：byte[] 字段
                    foreach (var f in fields)
                    {
                        try
                        {
                            object v = f.GetValue(iobj);
                            if (v == null) continue;
                            byte[] b = TryConvertToByteArray(v);
                            if (b != null && b.Length > 0)
                                return b;
                        }
                        catch { }
                    }
                    // 第二遍：string 字段（名称含 script/text/data/source）
                    foreach (var f in fields)
                    {
                        try
                        {
                            string fn = (f.Name ?? "").ToLowerInvariant();
                            if (!(fn.Contains("script") || fn.Contains("text") || fn.Contains("data") || fn.Contains("source")))
                                continue;
                            object v = f.GetValue(iobj);
                            if (v == null) continue;
                            string s = Il2CppObjToString(v);
                            if (!string.IsNullOrEmpty(s))
                                return Encoding.UTF8.GetBytes(s);
                        }
                        catch { }
                    }
                }
#else
                object raw = GetFieldOrProp(ta, "m_Script", "m_Data", "m_data", "_data", "m_Bytes", "_bytes", "m_TextData", "m_script");
                byte[] b2 = raw as byte[];
                if (b2 != null && b2.Length > 0)
                    return b2;
                string s3 = raw as string;
                if (!string.IsNullOrEmpty(s3))
                    return Encoding.UTF8.GetBytes(s3);
#endif
            }
            catch { }
            return null;
        }

        /// <summary>object → string（兼容 Il2CppSystem.String 代理）。</summary>
        private static string Il2CppObjToString(object o)
        {
            if (o == null) return null;
            string s = o as string;
            if (!string.IsNullOrEmpty(s)) return s;
            try { s = o.ToString(); } catch { s = null; }
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>获取对象的 il2cpp 类型名（MONO 下返回托管类型名）。</summary>
        private static string GetObjectTypeName(object o)
        {
            if (o == null) return "null";
#if CPP
            try
            {
                Il2CppSystem.Object io = o.TryCast<Il2CppSystem.Object>();
                if (io != null)
                {
                    string n = GetIl2CppTypeName(io);
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            catch { }
#endif
            try { return o.GetType().FullName ?? o.GetType().Name; }
            catch { return "?"; }
        }

        private static bool textAssetDiagLogged = false;

        /// <summary>
        /// Cubism 默认贴图命名：texture_00 / texture_00_1（前缀 texture_ + 纯数字/下划线）。
        /// </summary>
        private static bool IsCubismDefaultTextureName(string n)
        {
            if (string.IsNullOrEmpty(n) || n.Length <= "texture_".Length)
                return false;
            if (!n.StartsWith("texture_", System.StringComparison.OrdinalIgnoreCase))
                return false;
            for (int i = "texture_".Length; i < n.Length; i++)
            {
                char c = n[i];
                if (!(char.IsDigit(c) || c == '_'))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 按实例 ID 去重（用于缓存列表重复扫描后的清理）。
        /// </summary>
        private static List<T> DedupeByInstanceId<T>(List<T> list) where T : UnityEngine.Object
        {
            List<T> result = new();
            if (list == null) return result;
            HashSet<int> seen = new();
            foreach (T o in list)
            {
                try
                {
                    if (o == null) continue;
                    if (seen.Add(o.GetInstanceID()))
                        result.Add(o);
                }
                catch { }
            }
            return result;
        }

        #endregion

        #region moc3 长度解析

        /// <summary>
        /// 解析 moc3 文件头计算完整长度。
        /// moc3 结构: "MOC3"(4B) + reserved(4B) + count(4B) + count 个 [offset(4B) + size(4B)]
        /// 文件总长度 = 最后一个 section 的 offset + size
        /// </summary>
        public static int CalculateMoc3Length(byte[] data)
        {
            if (data == null || data.Length < 12)
                return data != null ? data.Length : 0;

            if (data[0] != 0x4D || data[1] != 0x4F || data[2] != 0x43 || data[3] != 0x33)
                return data.Length;

            try
            {
                int count = BitConverter.ToInt32(data, 8);
                if (count <= 0 || count > 256)
                    return data.Length;

                int maxEnd = 0;
                for (int i = 0; i < count; i++)
                {
                    int off = 12 + i * 8;
                    if (off + 8 > data.Length)
                        break;
                    int offset = BitConverter.ToInt32(data, off);
                    int size = BitConverter.ToInt32(data, off + 4);
                    if (offset > 0 && size > 0 && offset + size > maxEnd)
                        maxEnd = offset + size;
                }

                if (maxEnd > 0)
                    return maxEnd;
                return data.Length;
            }
            catch
            {
                return data.Length;
            }
        }

        #endregion

        #region moc3 结构解析（v16af：从 moc3 取贴图数量/画布等数据）

        /// <summary>moc3 最小解析结果</summary>
        public class Moc3Info
        {
            public int Version;          // 1=3.00, 2=3.03, 3=4.00, 4=4.02, 5=5.00
            public int ArtMeshCount;
            public int TextureCount;     // art_mesh.texture_indices 最大值 + 1
            public float CanvasWidth;
            public float CanvasHeight;
            public float PixelsPerUnit;
        }

        private const int Moc3SotCount = 160;      // Section Offset Table 条目数（160 × uint32）
        private const int Moc3SotOffset = 64;      // SOT 起始（紧跟 64 字节头）
        private const int Moc3BodyOffset = 1984;   // 数据体起始（64 对齐）
        // art_mesh.texture_indices 在 section 布局表中的索引是 39，SOT 槽位 = 索引 + 2（0=countInfo, 1=canvasInfo）
        private const int Moc3TexIndicesSotSlot = 41;

        /// <summary>
        /// v16af：解析 moc3 头部，取贴图数量（art_mesh.texture_indices 最大值+1）、
        /// ArtMesh 数、画布尺寸/_ppu。
        /// 格式来源：py-moc3（moc3-reader-re 的 Python 移植，社区逆向 Cubism 导出器）：
        ///   [0..64) 头；[64..704) SOT 160×u32；body 自 1984 起；
        ///   countInfo=23×i32（counts[4]=ArtMesh 数）+128B 对齐；canvas=ppu/ox/oy/w/h/flag。
        /// </summary>
        public static bool TryParseMoc3(byte[] data, out Moc3Info info)
        {
            info = null;
            try
            {
                if (data == null || data.Length < Moc3BodyOffset + 128) return false;
                if (data[0] != 0x4D || data[1] != 0x4F || data[2] != 0x43 || data[3] != 0x33) return false;

                Moc3Info r = new Moc3Info();
                r.Version = data[4];
                if (r.Version < 1 || r.Version > 5) return false;

                // Section Offset Table
                int[] sot = new int[Moc3SotCount];
                for (int i = 0; i < Moc3SotCount; i++)
                    sot[i] = BitConverter.ToInt32(data, Moc3SotOffset + i * 4);

                // Count Info（优先用 SOT[0]，通常 = 1984）
                int countOff = sot[0] > 0 && sot[0] + 128 <= data.Length ? sot[0] : Moc3BodyOffset;
                int artMeshCount = BitConverter.ToInt32(data, countOff + 4 * 4);
                if (artMeshCount < 0 || artMeshCount > 100000) return false;
                r.ArtMeshCount = artMeshCount;

                // Canvas Info（SOT[1]，通常 = countOff + 128）
                int canvasOff = sot[1] > 0 && sot[1] + 24 <= data.Length ? sot[1] : countOff + 128;
                r.PixelsPerUnit = BitConverter.ToSingle(data, canvasOff);
                r.CanvasWidth = BitConverter.ToSingle(data, canvasOff + 12);
                r.CanvasHeight = BitConverter.ToSingle(data, canvasOff + 16);

                // art_mesh.texture_indices → 贴图数量
                int texOff = sot[Moc3TexIndicesSotSlot];
                int maxIdx = -1;
                if (texOff > 0 && texOff + 4 * artMeshCount <= data.Length)
                {
                    for (int i = 0; i < artMeshCount; i++)
                    {
                        int v = BitConverter.ToInt32(data, texOff + i * 4);
                        if (v > maxIdx) maxIdx = v;
                    }
                }
                r.TextureCount = maxIdx >= 0 ? maxIdx + 1 : 0;

                info = r;
                return true;
            }
            catch { return false; }
        }

        /// <summary>从贴图名尾部提取数字索引（texture_00 → 0；不匹配返回 -1）</summary>
        private static int ExtractTextureIndex(Texture2D t)
        {
            try
            {
                if (t == null || string.IsNullOrEmpty(t.name)) return -1;
                string n = t.name;
                int i = n.Length;
                while (i > 0 && n[i - 1] >= '0' && n[i - 1] <= '9') i--;
                if (i < n.Length && i > 0 && (n[i - 1] == '_' || n[i - 1] == '-'))
                {
                    int v;
                    if (int.TryParse(n.Substring(i), out v)) return v;
                }
            }
            catch { }
            return -1;
        }

        /// <summary>按名称序号排序贴图（保证 model3.json 的 Textures[i] 与 moc3 贴图索引 i 对应）</summary>
        private static int CompareCubismTextureByIndex(Texture2D a, Texture2D b)
        {
            int ia = ExtractTextureIndex(a);
            int ib = ExtractTextureIndex(b);
            if (ia >= 0 && ib >= 0) return ia.CompareTo(ib);
            if (ia >= 0) return -1;
            if (ib >= 0) return 1;
            return 0;
        }

        #endregion

        #region 收集入口

        /// <summary>
        /// 从 GameObject 及其所有子对象收集所有可导出的资源（递归，含子对象）。
        /// 自动检测并处理 Live2D Cubism / Spine 组件。
        /// </summary>
        public static List<ExportableItem> CollectExportables(GameObject go)
        {
            ResetCollectionState();
            List<ExportableItem> items = new();

            if (!go)
                return items;

            CollectFromObjectRecursive(go, items, "", 0);

            return items;
        }

        /// <summary>
        /// 只收集当前对象的资源（不递归子对象，但 Live2D 模型会以模型为单位完整导出）。
        /// </summary>
        public static List<ExportableItem> CollectSingleObject(GameObject go)
        {
            ResetCollectionState();
            List<ExportableItem> items = new();
            if (!go)
                return items;

            string objPath = go.name;
            try { CollectFromSingleObject(go, items, objPath); } catch { }
            try { ProcessSpecialComponents(go, items, objPath); } catch { }

            return items;
        }

        /// <summary>
        /// 循环加载导出入口：对象子树 + 向上找 Cubism/Spine 模型根再扫一遍 + 全局缓存兜底。
        /// </summary>
        public static List<ExportableItem> CollectLive2DExportables(GameObject root)
        {
            ResetCollectionState();
            List<ExportableItem> items = new();
            if (root == null)
                return items;

            // 1. 对象子树扫描
            try
            {
                CollectFromObjectRecursive(root, items, "", 0);
            }
            catch { }

            // 2. 从对象向上找模型根（含 CubismModel / Spine 组件的祖先），再扫描其子树
            try
            {
                GameObject modelRoot = FindModelRoot(root);
                if (modelRoot != null && modelRoot != root)
                {
                    ExplorerCore.Log($"[Live2D] 向上找到模型根: {modelRoot.name}，扫描其子树");
                    CollectFromObjectRecursive(modelRoot, items, modelRoot.name, 0);
                }
            }
            catch { }

            // 3. 全局扫描兜底：所有 TextAsset（moc3/json）
            CollectGlobalTextAssets(items);

            // 4. 全局扫描兜底：所有 Texture2D（贴图）
            CollectGlobalTextures(items);

            return items;
        }

        private static void ResetCollectionState()
        {
            exportedNames.Clear();
            collectedTexIds.Clear();
            processedCubismModels.Clear();
            processedSpineComps.Clear();
            spineResizeAddedIds.Clear();
            scannedObjectCount = 0;
            textAssetScanCount = 0;
        }

        /// <summary>
        /// 从对象向上查找 Cubism / Spine 模型根节点。
        /// </summary>
        private static GameObject FindModelRoot(GameObject go)
        {
            Transform t = go != null ? go.transform : null;
            int guard = 0;
            while (t != null && guard++ < 20)
            {
                try
                {
                    Component[] comps = t.gameObject.GetComponents<Component>();
                    if (comps != null)
                    {
                        foreach (Component c in comps)
                        {
                            if (c == null) continue;
                            string tn = GetComponentTypeName(c);
                            if (string.IsNullOrEmpty(tn)) continue;
                            if (tn.Contains("CubismModel") || tn.Contains("CubismRenderController") ||
                                tn.Contains("SkeletonRenderer") || tn.Contains("SkeletonAnimation") ||
                                tn.Contains("SkeletonMecanim") || tn.Contains("SkeletonGraphic"))
                                return t.gameObject;
                        }
                    }
                }
                catch { }
                try { t = t.parent; } catch { break; }
            }
            return null;
        }

        /// <summary>
        /// 从缓存导出 TextAsset 数据（moc3/json 等）。若未扫描则先触发扫描。
        /// </summary>
        private static void CollectGlobalTextAssets(List<ExportableItem> items)
        {
            if (!AssetCache.Scanned)
                ScanAndCacheAssets();

            if (AssetCache.TextAssetBytes.Count == 0)
            {
                ExplorerCore.LogWarning($"[缓存] 未缓存到任何 TextAsset 数据（共 {AssetCache.TextAssetTotal} 个，读取成功 {AssetCache.TextAssetReadOK}，失败 {AssetCache.TextAssetReadFail}）");
                return;
            }

            int mocCount = 0, jsonCount = 0;
            foreach (var kv in AssetCache.TextAssetBytes)
            {
                try
                {
                    string name = kv.Key;
                    byte[] bytes = kv.Value;
                    if (bytes == null || bytes.Length == 0)
                        continue;

                    string ext = DetectBytesExtension(bytes, name);
                    if (ext == "moc3" || ext == "moc" || ext == "json" || ext == "csv")
                    {
                        byte[] captured = bytes;
                        if (!exportedNames.Contains(name + "_" + ext))
                        {
                            exportedNames.Add(name + "_" + ext);
                            items.Add(new ExportableItem
                            {
                                Name = name,
                                Type = $"Live2D Data (.{ext})",
                                Asset = null,
                                ExportAction = (path) => ExportBytes(captured, path, ext)
                            });
                            if (ext == "moc3" || ext == "moc") mocCount++;
                            else jsonCount++;
                        }
                    }
                }
                catch { }
            }
            ExplorerCore.Log($"[缓存] 全局兜底: moc3/moc {mocCount} 个, json/csv {jsonCount} 个");
        }

        /// <summary>
        /// 从缓存导出所有贴图（全局兜底）。若未扫描则先触发扫描。
        /// </summary>
        private static void CollectGlobalTextures(List<ExportableItem> items)
        {
            if (!AssetCache.Scanned)
                ScanAndCacheAssets();

            int added = 0;
            foreach (Texture2D tex in AssetCache.Textures)
            {
                try
                {
                    if (tex == null)
                        continue;
                    if (!AddTextureItem(tex, items, "Texture (.png)"))
                        continue;
                    added++;
                }
                catch { }
            }
            ExplorerCore.Log($"[缓存] 全局兜底贴图: 新增 {added} 个（共缓存 {AssetCache.Textures.Count} 个）");
        }

        /// <summary>
        /// 根据字节头和名称判断扩展名（独立于引擎对象，纯字节判断）
        /// </summary>
        private static string DetectBytesExtension(byte[] bytes, string name)
        {
            if (bytes != null && bytes.Length >= 4)
            {
                // moc3: "MOC3"
                if (bytes[0] == 0x4D && bytes[1] == 0x4F && bytes[2] == 0x43 && bytes[3] == 0x33)
                    return "moc3";
                // moc: "moc"（小写）+ 版本字节（Cubism 2）
                if (bytes[0] == 0x6D && bytes[1] == 0x6F && bytes[2] == 0x63)
                    return "moc";
                // json: {"
                if (bytes[0] == 0x7B && bytes[1] == 0x22)
                    return "json";
                // zip: PK
                if (bytes[0] == 0x50 && bytes[1] == 0x4B)
                    return "zip";
            }
            string n = name?.ToLower() ?? "";
            if (n.Contains(".moc3") || n.Contains("moc3")) return "moc3";
            if (n.Contains(".moc")) return "moc";
            if (n.Contains(".json")) return "json";
            if (n.Contains(".csv")) return "csv";
            return "txt";
        }

        #endregion

        #region 递归收集

        /// <summary>
        /// 递归收集对象及其子对象的资源（带深度和数量限制）
        /// </summary>
        private static void CollectFromObjectRecursive(GameObject go, List<ExportableItem> items, string pathPrefix, int depth)
        {
            if (!go)
                return;

            if (depth > MaxRecursionDepth)
                return;

            scannedObjectCount++;
            if (scannedObjectCount > MaxObjectsToScan)
                return;

            string objPath = string.IsNullOrEmpty(pathPrefix) ? go.name : $"{pathPrefix}/{go.name}";

            // 收集标准资源 + Live2D/Spine 特殊组件
            try
            {
                CollectFromSingleObject(go, items, objPath);
            }
            catch { }

            try
            {
                ProcessSpecialComponents(go, items, objPath);
            }
            catch { }

            // 递归子对象（每个子对象独立 try-catch）
            for (int i = 0; i < go.transform.childCount; i++)
            {
                try
                {
                    Transform child = go.transform.GetChild(i);
                    if (child)
                        CollectFromObjectRecursive(child.gameObject, items, objPath, depth + 1);
                }
                catch { }
            }
        }

        /// <summary>
        /// 从单个对象收集标准资源（Mesh/Texture/Audio，安全版：单次GetComponents，类型名判断）
        /// </summary>
        private static void CollectFromSingleObject(GameObject go, List<ExportableItem> items, string objPath)
        {
            if (go == null)
                return;

            Component[] components = null;
            try
            {
                components = go.GetComponents<Component>();
            }
            catch
            {
                return;
            }

            if (components == null)
                return;

            foreach (Component comp in components)
            {
                try
                {
                    if (comp == null)
                        continue;

                    // 必须用真实类型名（IL2CPP 下 GetType().Name 可能返回"Component"）
                    string typeName = GetComponentTypeName(comp);

                    // MeshFilter
                    if (typeName.Contains("MeshFilter"))
                    {
                        try
                        {
                            MeshFilter mf = comp.TryCast<MeshFilter>();
                            if (mf != null && mf.sharedMesh != null)
                            {
                                Mesh mesh = mf.sharedMesh;
                                items.Add(new ExportableItem
                                {
                                    Name = $"{objPath}_Mesh",
                                    Type = "Mesh (.fbx)",
                                    Asset = mesh,
                                    ExportAction = (path) => ExportMeshAsFBX(mesh, path)
                                });
                            }
                        }
                        catch { }
                    }
                    // SkinnedMeshRenderer
                    else if (typeName.Contains("SkinnedMeshRenderer"))
                    {
                        try
                        {
                            SkinnedMeshRenderer smr = comp.TryCast<SkinnedMeshRenderer>();
                            if (smr != null && smr.sharedMesh != null)
                            {
                                Mesh mesh = smr.sharedMesh;
                                items.Add(new ExportableItem
                                {
                                    Name = $"{objPath}_SkinnedMesh",
                                    Type = "Mesh (.fbx)",
                                    Asset = mesh,
                                    ExportAction = (path) => ExportMeshAsFBX(mesh, path)
                                });
                            }
                        }
                        catch { }
                    }
                    // SpriteRenderer
                    else if (typeName.Contains("SpriteRenderer"))
                    {
                        try
                        {
                            SpriteRenderer sr = comp.TryCast<SpriteRenderer>();
                            if (sr != null && sr.sprite != null && sr.sprite.texture != null)
                            {
                                Texture2D tex = sr.sprite.texture;
                                AddTextureItem(tex, items, "Texture (.png)");
                            }
                        }
                        catch { }
                    }
                    // MeshRenderer / Renderer（普通材质贴图；Live2D 的贴图走 ProcessSpecialComponents）
                    else if (typeName.Contains("MeshRenderer"))
                    {
                        try
                        {
                            Renderer renderer = comp.TryCast<Renderer>();
                            if (renderer != null)
                            {
                                Material[] mats = renderer.sharedMaterials;
                                if (mats != null)
                                {
                                    for (int i = 0; i < mats.Length; i++)
                                    {
                                        try
                                        {
                                            Material mat = mats[i];
                                            if (mat == null || mat.shader == null)
                                                continue;

                                            if (mat.HasProperty("_MainTex"))
                                            {
                                                Texture tex = mat.GetTexture("_MainTex");
                                                if (tex != null)
                                                {
                                                    Texture2D tex2d = tex.TryCast<Texture2D>();
                                                    if (tex2d != null)
                                                        AddTextureItem(tex2d, items, "Texture (.png)");
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    // AudioSource
                    else if (typeName.Contains("AudioSource"))
                    {
                        try
                        {
                            AudioSource audio = comp.TryCast<AudioSource>();
                            if (audio != null && audio.clip != null)
                            {
                                AudioClip clip = audio.clip;
                                string clipName = string.IsNullOrEmpty(clip.name) ? $"{objPath}_Audio" : clip.name;
                                items.Add(new ExportableItem
                                {
                                    Name = clipName,
                                    Type = "Audio (.wav)",
                                    Asset = clip,
                                    ExportAction = (path) => ExportAudioAsWAV(clip, path)
                                });
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// v16ag：全场景扫描 —— 收集场景里所有 Live2D Cubism / Spine 模型的导出项。
        /// 「全场景扫描」按钮入口：不依赖当前选中对象，遍历全部已加载 GameObject，
        /// 找到 CubismModel / Spine Skeleton 组件后向上找模型根，逐个走完整导出。
        /// </summary>
        public static List<ExportableItem> CollectAllLive2DSpineSceneModels()
        {
            ResetCollectionState();
            List<ExportableItem> items = new();
            HashSet<int> processedRoots = new();
            int cubism = 0, spine = 0;

            try
            {
                // RuntimeHelper.FindObjectsOfTypeAll 跨 Mono/IL2CPP/Unhollower 都安全（Live2DRecorder 同款）
                UnityEngine.Object[] all = RuntimeHelper.FindObjectsOfTypeAll(typeof(GameObject));
                foreach (UnityEngine.Object o in all)
                {
                    try
                    {
                        GameObject go = o != null ? o.TryCast<GameObject>() : null;
                        if (go == null) continue;

                        Component[] comps = null;
                        try { comps = go.GetComponents<Component>(); } catch { }
                        if (comps == null) continue;

                        foreach (Component comp in comps)
                        {
                            try
                            {
                                if (comp == null) continue;
                                string tn = GetComponentTypeName(comp);
                                if (string.IsNullOrEmpty(tn)) continue;

                                bool isCubism = tn.Contains("CubismModel");
                                bool isSpine = tn.Contains("SkeletonRenderer") || tn.Contains("SkeletonAnimation")
                                    || tn.Contains("SkeletonMecanim") || tn.Contains("SkeletonGraphic");
                                if (!isCubism && !isSpine) continue;

                                GameObject root = FindModelRoot(go);
                                if (root == null) continue;
                                int rid = root.GetInstanceID();
                                if (processedRoots.Contains(rid)) continue;
                                processedRoots.Add(rid);

                                if (isCubism) { cubism++; ExportCubismModel(comp, items); }
                                else { spine++; ExportSpineModel(comp, items, root.name); }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning("[Live2D] 全场景扫描异常: " + ex.Message);
            }

            ExplorerCore.Log($"[Live2D] 全场景扫描完成: Cubism {cubism} 个, Spine {spine} 个, 导出条目 {items.Count}");
            return items;
        }

        /// <summary>
        /// 处理 Live2D Cubism / Spine 特殊组件（按真实类型名分发）。
        /// </summary>
        private static void ProcessSpecialComponents(GameObject go, List<ExportableItem> items, string objPath)
        {
            Component[] comps;
            try { comps = go.GetComponents<Component>(); } catch { return; }
            if (comps == null) return;

            foreach (Component comp in comps)
            {
                try
                {
                    if (comp == null) continue;
                    string tn = GetComponentTypeName(comp);
                    if (string.IsNullOrEmpty(tn)) continue;

                    // Live2D Cubism 模型根组件 —— 完整导出（moc3 + 贴图 + model3.json）
                    if (tn.Contains("CubismModel"))
                    {
                        ExportCubismModel(comp, items);
                    }
                    // Spine 骨骼组件（Spine.Unity.SkeletonAnimation/SkeletonRenderer/SkeletonMecanim/SkeletonGraphic）
                    else if (tn.Contains("Spine") || tn.Contains("SkeletonRenderer")
                          || tn.Contains("SkeletonAnimation") || tn.Contains("SkeletonMecanim")
                          || tn.Contains("SkeletonGraphic"))
                    {
                        ExportSpineModel(comp, items, objPath);
                    }
                    // 其他 Cubism 控制器（Physics/Pose/Expression/Fade/Motion 等）
                    // —— 扫描其 TextAsset 字段，导出 physics3.json/pose3.json/exp3.json 等
                    else if (tn.Contains("Cubism") || tn.Contains("Live2D"))
                    {
                        if (tn.Contains("Controller") || tn.Contains("Motion") || tn.Contains("Expression")
                            || tn.Contains("Physics") || tn.Contains("Pose") || tn.Contains("Fade"))
                        {
                            CollectTextAssetFields(comp, items, objPath);
                        }
                    }
                }
                catch { }
            }
        }

        #endregion

        #region Live2D Cubism 导出（基于 Cubism SDK 4/5 官方源码字段链）

        /// <summary>
        /// 导出整个 Cubism 模型：moc3 + 贴图 + model3.json 清单。
        /// 关键字段链（Cubism SDK 4/5 官方源码）：
        ///   CubismModel._moc → CubismMoc._bytes (byte[]，moc3 原始字节)
        /// 贴图链：
        ///   CubismRenderer._mainTexture / MeshRenderer 的 MaterialPropertyBlock._MainTex / 材质 mainTexture
        /// </summary>
        private static void ExportCubismModel(Component modelComp, List<ExportableItem> items)
        {
            try
            {
                GameObject modelRoot = modelComp != null ? modelComp.gameObject : null;
                if (modelRoot == null) return;

                string modelName = string.IsNullOrEmpty(modelRoot.name) ? "cubism_model" : modelRoot.name;
                if (!processedCubismModels.Add(modelRoot.GetInstanceID()))
                    return; // 每个模型只处理一次

                ExplorerCore.Log($"[Live2D] 检测到 Cubism 模型: {modelName}");

                // ---- 1. moc3：CubismModel._moc → CubismMoc._bytes ----
                byte[] mocBytes = null;
                string mocKind = null; // "moc3" 或 "moc"(Cubism2)
                try
                {
                    object mocObj = GetFieldOrProp(modelComp, "_moc", "Moc");
                    if (mocObj != null)
                    {
                        DumpComponentValuesOnce(mocObj, "CubismMoc");
                        // Cubism 4/5 SDK：CubismMoc._bytes 就是 moc3 原始字节（byte[] 序列化字段）
                        object bytesVal = GetFieldOrProp(mocObj, "_bytes", "Bytes", "bytes", "_mocData", "mocData");
                        mocBytes = TryConvertToByteArray(bytesVal);
                        if (mocBytes == null)
                        {
                            // 兜底1：CubismMoc 自身就是被序列化的字节数组代理
                            mocBytes = TryConvertToByteArray(mocObj);
                        }
                        if (mocBytes == null)
                        {
                            // 兜底2：老版本 SDK 中 CubismMoc 引用 TextAsset
                            TextAsset mocTA = mocObj.TryCast<TextAsset>();
                            if (mocTA == null)
                            {
                                object taVal = GetFieldOrProp(mocObj, "MocSource", "_mocSource", "moc", "source", "_moc");
                                if (taVal != null) mocTA = taVal.TryCast<TextAsset>();
                            }
                            if (mocTA != null)
                                mocBytes = ReadTextAssetFull(mocTA);
                        }
                    }
                    else
                    {
                        ExplorerCore.LogWarning($"[Live2D] CubismModel '{modelName}' 未取到 Moc 引用（_moc/Moc 反射均为 null）");
                        DumpComponentValuesOnce(modelComp, "CubismModel");
                    }
                }
                catch (System.Exception ex)
                {
                    ExplorerCore.LogWarning($"[Live2D] moc3 反射异常: {ex.Message}");
                }

                // 校验 moc 数据头
                if (mocBytes != null && mocBytes.Length >= 4)
                {
                    if (mocBytes[0] == 0x4D && mocBytes[1] == 0x4F && mocBytes[2] == 0x43 && mocBytes[3] == 0x33)
                    {
                        // "MOC3" 头：按 section 表截取精确长度
                        int len = CalculateMoc3Length(mocBytes);
                        if (len > 0 && len < mocBytes.Length)
                        {
                            byte[] trimmed = new byte[len];
                            Array.Copy(mocBytes, 0, trimmed, 0, len);
                            mocBytes = trimmed;
                        }
                        mocKind = "moc3";
                    }
                    else if (mocBytes[0] == 0x6D && mocBytes[1] == 0x6F && mocBytes[2] == 0x63)
                    {
                        mocKind = "moc"; // Cubism 2
                    }
                    else
                    {
                        ExplorerCore.LogWarning($"[Live2D] moc 字节头不合法: {mocBytes[0]:X2} {mocBytes[1]:X2} {mocBytes[2]:X2} {mocBytes[3]:X2}（非 MOC3/moc），已忽略");
                        mocBytes = null;
                    }
                }

                // ---- 2. 贴图（多路径收集，实例ID去重）----
                // v16af：先解析 moc3 拿到「模型需要几张贴图」，收集后校验数量并按索引排序
                Moc3Info mocInfo = null;
                if (mocKind == "moc3" && mocBytes != null && TryParseMoc3(mocBytes, out mocInfo))
                {
                    string verDesc = mocInfo.Version >= 5
                        ? "Cubism 5.0（需新版查看器：Cubism Editor/Viewer 5.0+，旧核心会渲染异常）"
                        : "Cubism " + (mocInfo.Version >= 3 ? "4.0" : "3.x");
                    ExplorerCore.Log(string.Format(
                        "[Live2D] moc3 解析: {0}, ArtMesh={1}, 需要贴图数={2}, Canvas={3}x{4} ppu={5}",
                        verDesc, mocInfo.ArtMeshCount, mocInfo.TextureCount,
                        mocInfo.CanvasWidth.ToString("F0"), mocInfo.CanvasHeight.ToString("F0"),
                        mocInfo.PixelsPerUnit.ToString("F1")));
                }
                else if (mocKind == "moc3")
                {
                    ExplorerCore.LogWarning("[Live2D] moc3 结构解析失败，贴图数量校验跳过（仍按收集结果导出）");
                }

                List<Texture2D> textures = new();
                try
                {
                    CollectCubismTextures(modelRoot, modelComp, textures,
                        mocInfo != null ? mocInfo.TextureCount : 0);
                }
                catch (System.Exception ex)
                {
                    ExplorerCore.LogWarning($"[Live2D] 贴图收集异常: {ex.Message}");
                }

                // v16af：按名称序号排序（texture_00 → 索引0），保证 model3.json 的
                //   Textures[i] 与 moc3 内部贴图索引 i 一一对应（顺序错了渲染会张冠李戴）
                try { textures.Sort(CompareCubismTextureByIndex); } catch { }

                // v16af：数量校验 —— 以 moc3 数据为准
                if (mocInfo != null && mocInfo.TextureCount > 0)
                {
                    if (textures.Count == mocInfo.TextureCount)
                    {
                        ExplorerCore.Log("[Live2D] 贴图数量与 moc3 一致: " + textures.Count + " 张");
                    }
                    else
                    {
                        ExplorerCore.LogWarning("[Live2D] 贴图数量与 moc3 不符: 收集到 " + textures.Count
                            + " 张，moc3 需要 " + mocInfo.TextureCount
                            + " 张（继续按实际收集结果导出，请检查是否有贴图遗漏/多余）");
                    }
                }

                ExplorerCore.Log($"[Live2D] 模型 '{modelName}': moc3={(mocBytes != null ? mocBytes.Length + "字节" : "未取到")}, 贴图 {textures.Count} 张");

                // ---- 3. 生成导出条目 ----
                // 贴图（命名供 model3.json 引用）
                //   按首张贴图的 max(w, h) 作为 "第一个数字"，把所有贴图统一
                //   写入 "{modelName}.{dim}" 子目录（与 anim_converter.py 一致），
                //   model3.json 的 Textures 引用相应改为 "{modelName}.{dim}/xxx.png"。
                List<string> texFileNames = new();
                HashSet<string> texFileNamesLocal = new();  // v16ai：本模型内文件名去重
                string texSubdir = "";   // 子目录名后缀（{modelName}.{dim}），空表示不分子目录
                if (textures.Count > 0)
                {
                    int firstDim = 0;
                    for (int k = 0; k < textures.Count; k++)
                    {
                        if (textures[k] == null) continue;
                        int w = 0, h = 0;
                        try { w = textures[k].width; h = textures[k].height; } catch { }
                        if (w > 0 && h > 0) { firstDim = System.Math.Max(w, h); break; }
                    }
                    if (firstDim > 0)
                        texSubdir = modelName + "." + firstDim;
                }
                for (int i = 0; i < textures.Count; i++)
                {
                    Texture2D tex = textures[i];
                    string fileName = string.IsNullOrEmpty(tex.name) ? $"texture_{i:00}" : tex.name;
                    // 文件名去重：v16ai 起只在本模型内去重即可。
                    //   原实现拿 fileName 去比对 texFileNames（里面存的是含子目录的
                    //   "model.dim/xxx" 引用），根本比不中，等于没去重；跨模型也不该去重。
                    string baseName = fileName;
                    int dup = 1;
                    while (texFileNamesLocal.Contains(fileName))
                        fileName = baseName + "_" + (dup++);
                    texFileNamesLocal.Add(fileName);
                    // model3.json 引用使用相对路径（含子目录）
                    string jsonRefName = string.IsNullOrEmpty(texSubdir)
                        ? fileName
                        : (texSubdir + "/" + fileName);
                    texFileNames.Add(jsonRefName);

                    Texture2D captured = tex;
                    string capturedName = fileName;
                    // v16ai 关键修复：导出键必须带模型限定。
                    //   原来用 "texture_00_png" 这种纯文件名做键，第一个处理的模型
                    //   （如「立ち絵」）占用后，其余模型的同名贴图会整体被判为重复而
                    //   跳过，表现为“10 个模型只有 2 个导出完整”。
                    string exportKey = modelName + "/" + capturedName + "_png";
                    if (!exportedNames.Contains(exportKey))
                    {
                        exportedNames.Add(exportKey);
                        string capturedSubdir = texSubdir;   // 闭包变量
                        items.Add(new ExportableItem
                        {
                            Name = capturedName,          // 磁盘文件名（不含子目录）
                            Group = texSubdir,            // 列表里显示所属模型
                            Type = "Live2D Texture (.png)",
                            Asset = captured,
                            ExportAction = (path) => ExportTextureAsPNG(captured,
                                InjectResolutionSubdir(path, capturedSubdir))
                        });
                    }
                }
                if (!string.IsNullOrEmpty(texSubdir))
                {
                    int dim = 0;
                    try { dim = int.Parse(texSubdir.Substring(modelName.Length + 1)); } catch { }
                    ExplorerCore.Log($"[Live2D] 贴图 {textures.Count} 张 -> {texSubdir}/ (首张最大边 {dim})");
                }

                // ---- 3.6 关联音频：扫模型子树下所有 AudioSource.clip ----
                //   每个 AudioClip 单独导出为 .wav，并生成一份
                //   "{modelName}.audios.json" 清单（name/length/channels/sample_rate + 所在 AudioSource 名）。
                //   motion-audio 精确配对运行时无法可靠识别（需要 Animator StateBehaviour 反射），
                //   所以这里只列音频名清单，用户可按文件名匹配 motion。
                List<KeyValuePair<string, AudioClip>> finalAudioEntries = new();
                try
                {
                    AudioSource[] sources = modelRoot.GetComponentsInChildren<AudioSource>(true);
                    HashSet<int> audioIds = new();
                    List<KeyValuePair<string, AudioClip>> rawAudios = new();
                    foreach (AudioSource src in sources)
                    {
                        try
                        {
                            if (src == null || src.clip == null) continue;
                            int id = src.clip.GetInstanceID();
                            if (audioIds.Contains(id)) continue;
                            audioIds.Add(id);
                            AudioClip clip = src.clip;
                            string aname = string.IsNullOrEmpty(clip.name) ? ("audio_" + id) : clip.name;
                            rawAudios.Add(new KeyValuePair<string, AudioClip>(aname, clip));
                        }
                        catch { }
                    }
                    // 按 name 去重
                    HashSet<string> nameUsed = new();
                    foreach (var kv in rawAudios)
                    {
                        string aname = kv.Key;
                        string baseName = aname;
                        int dup = 1;
                        while (nameUsed.Contains(aname))
                            aname = baseName + "_" + (dup++);
                        nameUsed.Add(aname);
                        finalAudioEntries.Add(new KeyValuePair<string, AudioClip>(aname, kv.Value));
                    }
                }
                catch (System.Exception ex)
                {
                    ExplorerCore.LogWarning($"[Live2D] 音频收集异常: {ex.Message}");
                }

                // 导出每个 AudioClip 为 .wav
                foreach (var kv in finalAudioEntries)
                {
                    string aname = kv.Key;
                    AudioClip clip = kv.Value;
                    if (clip == null) continue;
                    // v16ai：音频导出键同样加模型限定，避免跨模型同名音频互相抑制
                    string audioKey = modelName + "/" + aname + "_wav";
                    if (!exportedNames.Contains(audioKey))
                    {
                        exportedNames.Add(audioKey);
                        AudioClip capturedClip = clip;
                        items.Add(new ExportableItem
                        {
                            Name = aname,
                            Group = modelName,          // v16ai：列表里显示所属模型
                            Type = "Live2D Audio (.wav)",
                            Asset = capturedClip,
                            ExportAction = (path) => ExportAudioAsWAV(capturedClip, path)
                        });
                    }
                }

                // 写一份 {modelName}.audios.json 清单（音频名 + 时长 + 采样率 + 声道数）
                if (finalAudioEntries.Count > 0)
                {
                    StringBuilder audioSb = new StringBuilder();
                    audioSb.AppendLine("{");
                    audioSb.AppendLine("  \"model\": \"" + EscapeJson(modelName) + "\",");
                    audioSb.AppendLine("  \"audio_count\": " + finalAudioEntries.Count + ",");
                    audioSb.Append("  \"audios\": [");
                    for (int i = 0; i < finalAudioEntries.Count; i++)
                    {
                        AudioClip c = finalAudioEntries[i].Value;
                        string n = finalAudioEntries[i].Key;
                        if (i > 0) audioSb.Append(",");
                        audioSb.AppendLine();
                        audioSb.Append("    {");
                        audioSb.Append("\"name\": \"" + EscapeJson(n) + "\"");
                        if (c != null)
                        {
                            try
                            {
                                audioSb.Append(", \"length_sec\": " + c.length.ToString("F3"));
                                audioSb.Append(", \"channels\": " + c.channels);
                                audioSb.Append(", \"sample_rate\": " + c.frequency);
                            }
                            catch { }
                        }
                        audioSb.Append("}");
                    }
                    audioSb.AppendLine();
                    audioSb.AppendLine("  ]");
                    audioSb.AppendLine("}");
                    string manifestJson = audioSb.ToString();
                    items.Add(new ExportableItem
                    {
                        Name = modelName + ".audios",
                        Type = "Live2D Audio Manifest (.audios.json)",
                        Asset = null,
                        ExportAction = (path) => ExportText(manifestJson, path, "json")
                    });
                    ExplorerCore.Log($"[Live2D] 音频 {finalAudioEntries.Count} 个 wav + 1 个 manifest (.audios.json)");
                }

                // moc3
                if (mocBytes != null && mocKind != null)
                {
                    byte[] capturedMoc = mocBytes;
                    string mocFile = modelName + "." + mocKind;
                    items.Add(new ExportableItem
                    {
                        Name = modelName,
                        Type = mocKind == "moc3" ? "Live2D Model (.moc3)" : "Live2D Model (.moc)",
                        Asset = null,
                        ExportAction = (path) => ExportBytes(capturedMoc, path, mocKind)
                    });

                    // ---- 3.5 motion3.json：从 Animator 的 AnimationClip 反向转换（尽力而为）----
                    List<string> motionFiles = new();
                    if (mocKind == "moc3")
                    {
                        ExportCubismMotions(modelRoot, modelName, items, motionFiles);
                    }

                    // ---- 4. model3.json 清单（可直接在 Live2D Viewer 打开）----
                    if (mocKind == "moc3")
                    {
                        string json = BuildModel3Json(modelName, mocFile, texFileNames, motionFiles, items);
                        items.Add(new ExportableItem
                        {
                            Name = modelName,
                            Type = "Live2D Manifest (.model3.json)",
                            Asset = null,
                            ExportAction = (path) => ExportText(json, path, "model3.json")
                        });
                    }
                }
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"[Live2D] Cubism 模型导出失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 把 {subdir} 子目录注入到 path 中（保持文件名不变）。
        /// 例如 "C:\export\tex" + "2233.2048" → "C:\export\2233.2048\tex"。
        /// subdir 为空时直接返回原 path，不做修改。
        /// 注意：net35 没有 Path.Combine 的 3 参数重载，必须用嵌套。
        /// </summary>
        private static string InjectResolutionSubdir(string path, string subdir)
        {
            if (string.IsNullOrEmpty(subdir)) return path;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                string name = System.IO.Path.GetFileName(path);
                if (string.IsNullOrEmpty(dir))
                    return System.IO.Path.Combine(subdir, name);
                return System.IO.Path.Combine(System.IO.Path.Combine(dir, subdir), name);
            }
            catch { return path; }
        }

        /// <summary>
        /// 收集 Cubism 模型的贴图（多路径）：
        /// 1. 子树所有 CubismRenderer 组件的 _mainTexture（Cubism SDK 官方字段）
        /// 2. 子树所有 Renderer 的 MaterialPropertyBlock._MainTex（Cubism 5 实际渲染路径）
        /// 3. 子树所有 Renderer 的 sharedMaterials.mainTexture
        /// 4. CubismModel 的 Textures/_textures 反射兜底（老版本 SDK）
        /// </summary>
        private static void CollectCubismTextures(GameObject modelRoot, Component modelComp, List<Texture2D> textures, int expectedTexCount = 0)
        {
            HashSet<int> ids = new();

            // v16af：moc3 要求的贴图数量；未解析出时退回旧的 <2 触发阈值
            int need = expectedTexCount > 0 ? expectedTexCount : 2;

            Action<Texture2D> add = (tex) =>
            {
                try
                {
                    if (tex == null) return;
                    int id = tex.GetInstanceID();
                    if (ids.Contains(id)) return;
                    // 过滤白色纹理（CubismRenderer getter 的默认回退值）和小图标
                    if (tex.width <= 8 || tex.height <= 8) return;
                    ids.Add(id);
                    textures.Add(tex);
                }
                catch { }
            };

            string modelName = string.IsNullOrEmpty(modelRoot.name) ? "" : modelRoot.name;

            // 4. CubismModel 反射兜底
            int cnt0 = textures.Count;
            try
            {
                object texArr = GetFieldOrProp(modelComp, "Textures", "_textures", "textures");
                if (texArr != null)
                {
                    foreach (object o in EnumerateObjects(texArr))
                    {
                        try { add(o != null ? o.TryCast<Texture2D>() : null); } catch { }
                    }
                }
            }
            catch { }
            int cntModel = textures.Count - cnt0;

            // 遍历子树
            int cnt1 = textures.Count;
            try
            {
                CollectCubismTexturesRecursive(modelRoot.transform, add, 0);
            }
            catch { }
            int cntSubtree = textures.Count - cnt1;

            // 5. 全局兜底：按模型名在所有已加载 Texture2D 中匹配。
            //    Cubism 贴图资源常命名为「模型名_00/模型名_01」，
            //    若渲染器路径拿到的数量不足 moc3 要求，尝试从全局池补齐。
            //    v16af：触发条件从 <2 改为 < need（moc3 要求的数量）；补齐数量封顶，避免同场景
            //    多个模型都叫 texture_00 时互相污染（老版 model3.json 出现 texture_00 重复 11 次的根因）。
            int cntGlobal = 0;
            if (textures.Count < need && !string.IsNullOrEmpty(modelName))
            {
                try
                {
                    List<Texture2D> pool = new(AssetCache.Textures);
                    if (pool.Count == 0)
                    {
                        // 缓存未建立时现场扫描
                        try
                        {
                            UnityEngine.Object[] all = null;
#if CPP
                            try
                            {
                                Il2CppSystem.Type il2cppType = Il2CppSystem.Type.GetType("UnityEngine.Texture2D, UnityEngine.CoreModule");
                                if (il2cppType != null)
                                    all = UnityEngine.Object.FindObjectsOfTypeAll(il2cppType);
                            }
                            catch { all = null; }
#else
                            all = UnityEngine.Object.FindObjectsOfTypeAll(typeof(Texture2D));
#endif
                            if (all != null)
                            {
                                foreach (UnityEngine.Object o in all)
                                {
                                    try
                                    {
                                        Texture2D t2 = o != null ? o.TryCast<Texture2D>() : null;
                                        if (t2 != null) pool.Add(t2);
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                    }

                    string lower = modelName.ToLowerInvariant();
                    List<string> diag = new();
                    foreach (Texture2D t in pool)
                    {
                        try
                        {
                            // v16af：补齐到 moc3 要求数量即停，防止其他模型的同名贴图混入
                            if (textures.Count >= need) break;
                            if (t == null || t.width <= 8 || t.height <= 8) continue;
                            string n = t.name ?? "";
                            if (string.IsNullOrEmpty(n)) continue;
                            // 匹配规则：① 贴图名包含模型名；② Cubism 默认命名 texture_00 / texture_00_1（后缀纯数字下划线，尺寸≥256）
                            if (n.ToLowerInvariant().Contains(lower) ||
                                (IsCubismDefaultTextureName(n) && t.width >= 256 && t.height >= 256))
                            {
                                add(t);
                                cntGlobal++;
                            }
                            else if (diag.Count < 30 && t.width > 64 &&
                                     n.ToLowerInvariant().Contains("texture"))
                            {
                                diag.Add($"{n}({t.width}x{t.height})");
                            }
                        }
                        catch { }
                    }
                    if (textures.Count < need && diag.Count > 0)
                        ExplorerCore.Log($"[Live2D] 贴图不足（需要 {need}），全局候选参考: {string.Join(", ", diag.ToArray())}");
                }
                catch (System.Exception ex)
                {
                    ExplorerCore.LogWarning($"[Live2D] 全局贴图兜底异常: {ex.Message}");
                }
            }

            ExplorerCore.Log($"[Live2D] 贴图收集 '{modelName}': 模型字段 +{cntModel}, 子树渲染器 +{cntSubtree}, 全局按名匹配 +{cntGlobal}, 合计 {textures.Count} 张 (moc3 需要 {need})");
        }

        private static void CollectCubismTexturesRecursive(Transform t, Action<Texture2D> add, int depth)
        {
            if (t == null || depth > 20) return;

            Component[] comps = null;
            try { comps = t.gameObject.GetComponents<Component>(); } catch { }
            if (comps != null)
            {
                foreach (Component comp in comps)
                {
                    try
                    {
                        if (comp == null) continue;
                        string tn = GetComponentTypeName(comp);

                        // 1. CubismRenderer._mainTexture（官方字段）
                        if (tn.Contains("CubismRenderer"))
                        {
                            object texVal = GetFieldOrProp(comp, "_mainTexture", "MainTexture", "mainTexture");
                            if (texVal != null)
                                add(texVal.TryCast<Texture2D>());
                        }

                        // 2/3. Renderer：MaterialPropertyBlock + 材质
                        Renderer r = comp.TryCast<Renderer>();
                        if (r != null)
                        {
#if !UNHOLLOWER
                            // MaterialPropertyBlock（Cubism 5 SDK 通过它应用贴图，材质上为 null）
                            // 注：Unhollower 代理库无 MPB 无参构造，该路径仅 Interop/Mono 可用
                            try
                            {
                                MaterialPropertyBlock block = new MaterialPropertyBlock();
                                r.GetPropertyBlock(block);
                                Texture pbTex = block.GetTexture("_MainTex");
                                if (pbTex == null)
                                    pbTex = block.GetTexture("_MainTexture");
                                if (pbTex != null)
                                    add(pbTex.TryCast<Texture2D>());
                            }
                            catch { }
#endif

                            // 材质贴图（多个常见属性名）
                            try
                            {
                                Material[] mats = r.sharedMaterials;
                                if (mats != null)
                                {
                                    string[] texProps = { "_MainTex", "_MainTexture", "_Texture", "_Tex" };
                                    foreach (Material mat in mats)
                                    {
                                        try
                                        {
                                            if (mat == null) continue;
                                            Texture mt = mat.mainTexture;
                                            if (mt == null)
                                            {
                                                foreach (string prop in texProps)
                                                {
                                                    try
                                                    {
                                                        if (mat.HasProperty(prop))
                                                        {
                                                            mt = mat.GetTexture(prop);
                                                            if (mt != null) break;
                                                        }
                                                    }
                                                    catch { }
                                                }
                                            }
                                            if (mt != null)
                                                add(mt.TryCast<Texture2D>());
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }

            for (int i = 0; i < t.childCount; i++)
            {
                try
                {
                    Transform child = t.GetChild(i);
                    if (child != null)
                        CollectCubismTexturesRecursive(child, add, depth + 1);
                }
                catch { }
            }
        }

        /// <summary>
        /// 生成标准 Live2D model3.json（Live2D Viewer 可直接加载）。
        /// 标准顶层字段：Version / FileReferences(Moc/Textures/Motions/Physics/Pose/Expressions) / Groups / HitAreas。
        /// motionFiles 为本次从 AnimationClip 反向生成的 motion3.json 文件名。
        /// </summary>
        private static string BuildModel3Json(string modelName, string mocFile, List<string> texFileNames, List<string> motionFiles, List<ExportableItem> items)
        {
            try
            {
                // 从已收集的导出项里找关联的 physics3/pose3/exp3 json
                List<string> physics = new();
                List<string> poses = new();
                List<string> expressions = new();
                foreach (ExportableItem it in items)
                {
                    string n = (it.Name ?? "").ToLower();
                    if (n.Contains("physics3"))
                        physics.Add(it.Name + ".json");
                    else if (n.Contains("pose3"))
                        poses.Add(it.Name + ".json");
                    else if (n.Contains("exp3"))
                        expressions.Add(it.Name + ".json");
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"Version\": 3,");
                sb.AppendLine("  \"FileReferences\": {");
                sb.AppendLine("    \"Moc\": \"" + EscapeJson(mocFile) + "\",");

                // 贴图
                StringBuilder texSb = new StringBuilder();
                for (int i = 0; i < texFileNames.Count; i++)
                {
                    if (i > 0) texSb.Append(", ");
                    texSb.Append("\"" + EscapeJson(texFileNames[i]) + ".png\"");
                }
                sb.AppendLine("    \"Textures\": [" + texSb.ToString() + "]");

                if (physics.Count > 0)
                    sb.AppendLine("    ,\"Physics\": \"" + EscapeJson(physics[0]) + "\"");
                if (poses.Count > 0)
                    sb.AppendLine("    ,\"Pose\": \"" + EscapeJson(poses[0]) + "\"");
                if (expressions.Count > 0)
                {
                    sb.Append("    ,\"Expressions\": [");
                    for (int i = 0; i < expressions.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append("{\"Name\": \"" + EscapeJson(expressions[i]) + "\", \"File\": \"" + EscapeJson(expressions[i]) + "\"}");
                    }
                    sb.AppendLine("]");
                }
                if (motionFiles != null && motionFiles.Count > 0)
                {
                    sb.Append("    ,\"Motions\": {");
                    sb.Append("\"Idle\": [");
                    for (int i = 0; i < motionFiles.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append("{\"File\": \"" + EscapeJson(motionFiles[i]) + "\"}");
                    }
                    sb.AppendLine("]}");
                }
                sb.AppendLine("  },");

                // Groups / HitAreas：运行时无法精确还原，输出空数组（官方查看器可接受）
                sb.AppendLine("  \"Groups\": [],");
                sb.AppendLine("  \"HitAreas\": []");
                sb.AppendLine("}");
                return sb.ToString();
            }
            catch
            {
                return "{\n  \"Version\": 3,\n  \"FileReferences\": {\n    \"Moc\": \"" + EscapeJson(mocFile) + "\"\n  }\n}";
            }
        }

        private static string EscapeJson(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        #region Live2D motion3.json 转换（AnimationClip → motion3）

        /// <summary>
        /// motion3 曲线结构（扁平 Segments 数组）。
        /// </summary>
        private sealed class Motion3Curve
        {
            public string Target;             // "Parameter" | "PartOpacity" | "Model"
            public string Id;
            public List<float> Segments = new(); // 扁平数组：起始点 [t,v] + 段
            public int SegmentCount;
        }

        /// <summary>
        /// 从 Animator 的 AnimationClip 反向生成 motion3.json。
        /// Live2D 动画在运行时已被 SDK 转成 AnimationClip，游戏内没有原始 .motion3.json 文件，
        /// 只能从 AnimationClip 的曲线（AnimationCurve）按官方 Segments 编码反向转换。
        /// 参考 AssetStudioMod 的 CubismMotion3Converter 思路；尽力而为，失败则降级跳过。
        /// </summary>
        private static void ExportCubismMotions(GameObject modelRoot, string modelName, List<ExportableItem> items, List<string> motionFiles)
        {
            try
            {
                // 优先路径：CubismFadeMotionList / CubismFadeMotionData（SDK 以托管序列化字段
                // 保存 motionName/parameterIds/parameterCurves，比 AnimationClip 反向转换可靠）
                if (ExportCubismFadeMotions(modelRoot, modelName, items, motionFiles))
                    return;

                Component animator = FindComponentByTypeName(modelRoot, "Animator");
                if (animator == null)
                {
                    ExplorerCore.Log($"[Live2D] '{modelName}' 未找到 Animator 组件，跳过 motion3 导出");
                    return;
                }

                Dictionary<string, KeyValuePair<string, string>> paramMap = BuildCubismParamMap(modelRoot);

                // Animator.runtimeAnimatorController.animationClips
                object controller = GetFieldOrProp(animator, "runtimeAnimatorController", "RuntimeAnimatorController", "_runtimeAnimatorController");
                object clipsArr = controller != null
                    ? GetFieldOrProp(controller, "animationClips", "AnimationClips", "_animationClips")
                    : null;
                if (clipsArr == null)
                {
                    ExplorerCore.Log($"[Live2D] '{modelName}' 未取到 AnimationClip 数组（runtimeAnimatorController.animationClips 反射失败）");
                    return;
                }

                List<object> clips = EnumerateObjects(clipsArr);
                if (clips.Count == 0)
                {
                    ExplorerCore.Log($"[Live2D] '{modelName}' Animator 无 AnimationClip");
                    return;
                }

                int exported = 0;
                int noCurves = 0;
                ExplorerCore.Log($"[Live2D] '{modelName}' 取到 {clips.Count} 个 AnimationClip");
                foreach (object clipObj in clips)
                {
                    try
                    {
                        if (clipObj == null) continue;
                        string clipName = SafeString(GetFieldOrProp(clipObj, "name", "Name"), "motion");

                        Motion3Curve[] curves = ConvertClipToMotion3Curves(clipObj, paramMap, out float duration, out float fps, out bool loop);
                        if (curves == null || curves.Length == 0)
                        {
                            noCurves++;
                            continue;
                        }

                        string motionJson = SerializeMotion3Json(curves, duration, fps, loop);
                        if (string.IsNullOrEmpty(motionJson))
                            continue;

                        // motion3 文件名（去掉 .anim 后缀）
                        string motionFile = clipName;
                        if (motionFile.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                            motionFile = motionFile.Substring(0, motionFile.Length - 5);
                        if (!motionFile.EndsWith(".motion3.json", StringComparison.OrdinalIgnoreCase))
                            motionFile += ".motion3.json";

                        string capturedJson = motionJson;
                        items.Add(new ExportableItem
                        {
                            Name = clipName,
                            Type = "Live2D Motion (.motion3.json)",
                            Asset = null,
                            ExportAction = (path) => ExportText(capturedJson, path, "motion3.json")
                        });
                        motionFiles.Add(motionFile);
                        exported++;
                        ExplorerCore.Log($"[Live2D] motion3 已生成: {motionFile}（{curves.Length} 条曲线）");
                    }
                    catch (System.Exception ex)
                    {
                        ExplorerCore.LogWarning($"[Live2D] motion3 转换失败: {ex.Message}");
                    }
                }
                ExplorerCore.Log($"[Live2D] '{modelName}' 共导出 {exported} 个 motion3.json" +
                    (noCurves > 0 ? $"（{noCurves} 个 clip 曲线为空被跳过）" : ""));

                // Animator 反向转换失败 → 终极兜底：从 TextAsset 缓存直出原始 motion3/physics/pose 等文件
                if (exported == 0)
                    ExportCubismMotionsFromTextAssets(modelName, items, motionFiles);
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"[Live2D] motion3 导出异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 从 CubismFadeMotionList / CubismFadeMotionData 导出 motion3.json。
        /// Cubism SDK 4/5 的淡入淡出运动数据以托管序列化字段保存
        /// （motionName / fadeInTime / fadeOutTime / motionLength / parameterIds / parameterCurves），
        /// 不经过原生曲线烘焙，比 Animator 的 AnimationClip 反向转换可靠得多。
        /// 返回 true 表示已走此路径（无论导出数量）。
        /// </summary>
        private static bool ExportCubismFadeMotions(GameObject modelRoot, string modelName, List<ExportableItem> items, List<string> motionFiles)
        {
            try
            {
                // 子树中找含 "Fade" 的组件，再在其字段里找 CubismFadeMotionList（ScriptableObject）
                object fadeListObj = FindFadeMotionList(modelRoot.transform, 0);
                if (fadeListObj == null)
                {
                    // 子树没有 → 全局扫描所有 ScriptableObject（可能挂在管理器/全局对象上）
                    fadeListObj = FindFadeMotionListGlobal();
                }
                if (fadeListObj == null)
                {
                    ExplorerCore.Log($"[Live2D] '{modelName}' 未找到 CubismFadeMotionList（子树+全局均无），改用 Animator 转换");
                    return false;
                }

                object motionsArr = GetFieldOrProp(fadeListObj,
                    "motionData", "MotionData", "_motionData", "motions", "Motions", "_motions");
                if (motionsArr == null)
                {
                    DumpComponentValuesOnce(fadeListObj, "CubismFadeMotionList");
                    ExplorerCore.LogWarning("[Live2D] CubismFadeMotionList 无 motionData 数组字段");
                    return false;
                }
                List<object> motions = EnumerateObjects(motionsArr);
                if (motions.Count == 0)
                {
                    ExplorerCore.Log("[Live2D] CubismFadeMotionList 的 motionData 为空数组");
                    return false;
                }

                ExplorerCore.Log($"[Live2D] '{modelName}' CubismFadeMotionList: {motions.Count} 个动作");

                int exported = 0;
                foreach (object m in motions)
                {
                    try
                    {
                        if (m == null) continue;
                        string motionName = SafeString(GetFieldOrProp(m,
                            "motionName", "MotionName", "_motionName", "name", "Name"), null);
                        if (string.IsNullOrEmpty(motionName)) motionName = "motion_" + exported;
                        float motionLength = ToFloat(GetFieldOrProp(m,
                            "motionLength", "MotionLength", "_motionLength", "length", "Length"), 0f);

                        object idsArr = GetFieldOrProp(m, "parameterIds", "ParameterIds", "_parameterIds");
                        object curvesArr = GetFieldOrProp(m, "parameterCurves", "ParameterCurves", "_parameterCurves");
                        if (curvesArr == null) continue;
                        List<object> curves = EnumerateObjects(curvesArr);
                        List<object> ids = idsArr != null ? EnumerateObjects(idsArr) : new List<object>();
                        if (curves.Count == 0) continue;

                        List<Motion3Curve> m3curves = new();
                        float maxEnd = 0f;
                        for (int i = 0; i < curves.Count; i++)
                        {
                            try
                            {
                                RawKey[] keys = ReadCurveKeysLateBound(curves[i]);
                                if (keys == null || keys.Length == 0) continue;
                                Motion3Curve mc = new Motion3Curve();
                                mc.Target = "Parameter";
                                mc.Id = i < ids.Count ? SafeString(ids[i], "Param" + i) : "Param" + i;
                                if (EncodeCurveSegments(keys, mc.Segments, out int segCount))
                                {
                                    mc.SegmentCount = segCount;
                                    m3curves.Add(mc);
                                    if (keys[keys.Length - 1].Time > maxEnd)
                                        maxEnd = keys[keys.Length - 1].Time;
                                }
                            }
                            catch { }
                        }
                        if (m3curves.Count == 0) continue;

                        float duration = motionLength > 0f ? motionLength : maxEnd;
                        string json = SerializeMotion3Json(m3curves.ToArray(), duration, 30f, true);
                        if (string.IsNullOrEmpty(json)) continue;

                        // 文件名：去路径、去 .motion3.json 后缀
                        string baseName = motionName.Replace('\\', '/');
                        int slash = baseName.LastIndexOf('/');
                        if (slash >= 0) baseName = baseName.Substring(slash + 1);
                        if (baseName.EndsWith(".motion3.json", StringComparison.OrdinalIgnoreCase))
                            baseName = baseName.Substring(0, baseName.Length - 13);

                        string capturedJson = json;
                        string capturedName = baseName;
                        items.Add(new ExportableItem
                        {
                            Name = capturedName,
                            Type = "Live2D Motion (.motion3.json)",
                            Asset = null,
                            ExportAction = (path) => ExportText(capturedJson, path, "motion3.json")
                        });
                        motionFiles.Add(capturedName + ".motion3.json");
                        exported++;
                    }
                    catch { }
                }
                ExplorerCore.Log($"[Live2D] '{modelName}' FadeMotion 路径共导出 {exported} 个 motion3.json");
                return exported > 0;
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"[Live2D] FadeMotion 导出异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 全局扫描所有已加载 ScriptableObject，找类型名含 "CubismFade" 的对象（CubismFadeMotionList 等）。
        /// </summary>
        private static object FindFadeMotionListGlobal()
        {
            try
            {
#if CPP
                Il2CppSystem.Type soType = Il2CppSystem.Type.GetType("UnityEngine.ScriptableObject, UnityEngine.CoreModule");
                if (soType == null) return null;
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfTypeAll(soType);
                if (all == null) return null;
                foreach (UnityEngine.Object o in all)
                {
                    try
                    {
                        if (o == null) continue;
                        Il2CppSystem.Object io = o.TryCast<Il2CppSystem.Object>();
                        if (io == null) continue;
                        string tn = GetIl2CppTypeName(io) ?? "";
                        if (tn.Contains("CubismFade"))
                        {
                            DumpComponentValuesOnce(io, "CubismFade(SO)");
                            return io;
                        }
                    }
                    catch { }
                }
#endif
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 兜底：从 TextAsset 缓存中直出原始 model3.json 引用的 motion3/physics3/pose3/exp3 文件。
        /// 原理：缓存里若有 model3.json（内容含 FileReferences 且含模型名），
        /// 解析其 FileReferences 的所有 File 引用，再按文件名反查 TextAsset 缓存。
        /// 找到的是游戏自带的原始文件，质量远超反向转换。
        /// </summary>
        private static void ExportCubismMotionsFromTextAssets(string modelName, List<ExportableItem> items, List<string> motionFiles)
        {
            try
            {
                if (AssetCache.TextAssetBytes.Count == 0)
                {
                    ExplorerCore.Log("[Live2D] TextAsset 缓存为空，跳过原始文件兜底");
                    return;
                }

                // 1) 找 model3.json：内容含 "FileReferences" 且包含模型名
                string model3Content = null;
                foreach (var kv in AssetCache.TextAssetBytes)
                {
                    try
                    {
                        if (kv.Value == null || kv.Value.Length == 0 || kv.Value.Length > 500_000) continue;
                        string c = Encoding.UTF8.GetString(kv.Value);
                        if (c.Contains("\"FileReferences\"") && c.IndexOf(modelName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            model3Content = c;
                            ExplorerCore.Log($"[Live2D] 找到原始 model3.json（缓存键: {kv.Key}）");
                            break;
                        }
                    }
                    catch { }
                }
                if (model3Content == null)
                {
                    ExplorerCore.Log($"[Live2D] TextAsset 缓存中未找到 '{modelName}' 的 model3.json");
                    return;
                }

                // 2) 解析所有 File 引用，反查缓存并生成导出项
                List<string> fileRefs = ExtractJsonFileRefs(model3Content);
                int exported = 0;
                HashSet<string> added = new(StringComparer.OrdinalIgnoreCase);
                foreach (string file in fileRefs)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(file) || !added.Add(file)) continue;
                        string lower = file.ToLowerInvariant();
                        // 跳过 moc（已有内存版）和贴图
                        if (lower.EndsWith(".moc3") || lower.EndsWith(".moc") || lower.EndsWith(".png")) continue;

                        byte[] data = FindTextAssetBytesByName(file);
                        if (data == null) continue;

                        bool isMotion = lower.EndsWith(".motion3.json");
                        // 显示名/导出名：带完整扩展名（原始字节直写）
                        string baseName = file.Replace('\\', '/');
                        int slash = baseName.LastIndexOf('/');
                        if (slash >= 0) baseName = baseName.Substring(slash + 1);

                        string typeLabel = isMotion ? "Live2D Motion (.motion3.json)"
                            : lower.EndsWith(".physics3.json") ? "Live2D Physics (.physics3.json)"
                            : lower.EndsWith(".pose3.json") ? "Live2D Pose (.pose3.json)"
                            : lower.EndsWith(".exp3.json") ? "Live2D Expression (.exp3.json)"
                            : "Live2D Config (.json)";

                        byte[] captured = data;
                        string fileName = baseName;
                        items.Add(new ExportableItem
                        {
                            Name = fileName,
                            Type = typeLabel,
                            Asset = null,
                            ExportAction = (path) => File.WriteAllBytes(EnsureValidPath(path), captured)
                        });
                        if (isMotion)
                            motionFiles.Add(baseName);
                        exported++;
                    }
                    catch { }
                }

                // 3) 原始 model3.json 本身也加入导出（与生成的清单并存，可在列表里自行勾选）
                if (exported > 0)
                {
                    byte[] m3 = Encoding.UTF8.GetBytes(model3Content);
                    byte[] capturedM3 = m3;
                    items.Add(new ExportableItem
                    {
                        Name = modelName + ".model3.json",
                        Type = "Live2D Manifest (.model3.json)",
                        Asset = null,
                        ExportAction = (path) => File.WriteAllBytes(EnsureValidPath(path), capturedM3)
                    });
                }

                ExplorerCore.Log($"[Live2D] 原始文件兜底：从 model3.json 反查导出 {exported} 个关联文件");
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"[Live2D] 原始文件兜底异常: {ex.Message}");
            }
        }

        /// <summary>提取 JSON 中所有 "File": "xxx" 的值（简单文本解析，无需 JSON 库）。</summary>
        private static List<string> ExtractJsonFileRefs(string json)
        {
            List<string> refs = new();
            try
            {
                int idx = 0;
                while (true)
                {
                    int f = json.IndexOf("\"File\"", idx, StringComparison.Ordinal);
                    if (f < 0) break;
                    int colon = json.IndexOf(':', f);
                    if (colon < 0) break;
                    int q1 = json.IndexOf('"', colon);
                    if (q1 < 0) break;
                    int q2 = json.IndexOf('"', q1 + 1);
                    if (q2 < 0) break;
                    refs.Add(json.Substring(q1 + 1, q2 - q1 - 1));
                    idx = q2 + 1;
                }
            }
            catch { }
            return refs;
        }

        /// <summary>按文件名反查 TextAsset 缓存（尝试原始名 / 去 .json / 再去一层扩展）。</summary>
        private static byte[] FindTextAssetBytesByName(string file)
        {
            try
            {
                string f = file.Replace('\\', '/');
                int slash = f.LastIndexOf('/');
                string nameOnly = slash >= 0 ? f.Substring(slash + 1) : f;

                List<string> candidates = new() { nameOnly };
                if (nameOnly.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    string noJson = nameOnly.Substring(0, nameOnly.Length - 5); // foo.motion3
                    candidates.Add(noJson);
                    int dot = noJson.LastIndexOf('.');
                    if (dot > 0)
                        candidates.Add(noJson.Substring(0, dot)); // foo
                }

                foreach (var kv in AssetCache.TextAssetBytes)
                {
                    foreach (string cand in candidates)
                    {
                        if (string.Equals(kv.Key, cand, StringComparison.OrdinalIgnoreCase))
                            return kv.Value;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 在子树中查找 CubismFadeMotionList：找含 "Fade" 的组件，枚举其字段值类型名匹配。
        /// </summary>
        private static object FindFadeMotionList(Transform t, int depth)
        {
            if (t == null || depth > 20) return null;

            Component[] comps = null;
            try { comps = t.gameObject.GetComponents<Component>(); } catch { }
            if (comps != null)
            {
                foreach (Component comp in comps)
                {
                    try
                    {
                        if (comp == null) continue;
                        string tn = GetComponentTypeName(comp);
                        if (string.IsNullOrEmpty(tn) || !tn.Contains("Fade")) continue;

                        foreach (string fieldName in GetAllFieldNames(comp))
                        {
                            try
                            {
                                object v = GetFieldOrProp(comp, fieldName);
                                if (v == null) continue;
                                string vtn = GetObjectTypeName(v);
                                if (vtn.Contains("CubismFadeMotionList"))
                                {
                                    DumpComponentValuesOnce(v, "CubismFadeMotionList");
                                    return v;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }

            for (int i = 0; i < t.childCount; i++)
            {
                try
                {
                    object r = FindFadeMotionList(t.GetChild(i), depth + 1);
                    if (r != null) return r;
                }
                catch { }
            }
            return null;
        }

        /// <summary>枚举对象的全部字段名（IL2CPP: il2cpp 反射；MONO: System 反射）。</summary>
        private static List<string> GetAllFieldNames(object obj)
        {
            List<string> names = new();
            if (obj == null) return names;
#if CPP
            try
            {
                Il2CppSystem.Object iobj = obj.TryCast<Il2CppSystem.Object>();
                if (iobj != null)
                {
                    Il2CppSystem.Type t = iobj.GetIl2CppType();
                    if (t != null)
                    {
                        var flags = Il2CppSystem.Reflection.BindingFlags.Instance
                            | Il2CppSystem.Reflection.BindingFlags.Public
                            | Il2CppSystem.Reflection.BindingFlags.NonPublic;
                        var fs = t.GetFields(flags);
                        if (fs != null)
                        {
                            foreach (var f in fs)
                            {
                                try { names.Add(f.Name); } catch { }
                            }
                        }
                    }
                }
            }
            catch { }
#else
            try
            {
                foreach (var f in obj.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    names.Add(f.Name);
            }
            catch { }
#endif
            return names;
        }

        /// <summary>
        /// 托管关键帧。不直接构造 UnityEngine.Keyframe——
        /// Unhollower 代理库没有 Keyframe 构造函数（仅 Interop/Mono 有），但 get_time 等属性都有。
        /// </summary>
        private struct RawKey
        {
            public float Time, Value, InT, OutT;
        }

        private static RawKey[] KeysToRaw(Keyframe[] ks)
        {
            if (ks == null || ks.Length == 0) return null;
            RawKey[] raw = new RawKey[ks.Length];
            for (int i = 0; i < ks.Length; i++)
            {
                raw[i].Time = ks[i].time;
                raw[i].Value = ks[i].value;
                raw[i].InT = ks[i].inTangent;
                raw[i].OutT = ks[i].outTangent;
            }
            return raw;
        }

        /// <summary>
        /// 晚绑定读取 AnimationCurve 的关键帧：优先 m_Curve 字段（Keyframe[]），
        /// 逐键反射 time/value/inTangent/outTangent（兼容曲线属性被剥离的情况）。
        /// </summary>
        private static RawKey[] ReadCurveKeysLateBound(object curveObj)
        {
            try
            {
                if (curveObj == null) return null;

                // 优先强类型 keys 属性（可用时最快）
                UnityEngine.AnimationCurve curve = curveObj as UnityEngine.AnimationCurve;
                if (curve != null)
                {
                    try
                    {
                        RawKey[] ks = KeysToRaw(curve.keys);
                        if (ks != null && ks.Length > 0) return ks;
                    }
                    catch { }
                }

#if CPP
                Il2CppSystem.Object iobj = curveObj.TryCast<Il2CppSystem.Object>();
                if (iobj == null) return null;
                Il2CppSystem.Type ct = iobj.GetIl2CppType();
                if (ct == null) return null;
                var flags = Il2CppSystem.Reflection.BindingFlags.Instance
                    | Il2CppSystem.Reflection.BindingFlags.Public
                    | Il2CppSystem.Reflection.BindingFlags.NonPublic;

                object arr = null;
                foreach (string fname in new[] { "m_Curve", "keys" })
                {
                    try
                    {
                        var f = ct.GetField(fname, flags);
                        if (f != null) { arr = f.GetValue(iobj); if (arr != null) break; }
                    }
                    catch { }
#if INTEROP
                    // 注意：Il2Cpp PropertyInfo 的 GetValue 重载集因后端而异——
                    // Interop 只有单参 GetValue(object)；Unhollower 无此重载，属性路径仅 Interop 可用。
                    try
                    {
                        var p = ct.GetProperty(fname, flags);
                        if (p != null) { arr = p.GetValue(iobj); if (arr != null) break; }
                    }
                    catch { }
#endif
                }
                if (arr == null) return null;

                List<object> elems = EnumerateObjects(arr);
                if (elems.Count == 0) return null;

                List<RawKey> keys = new();
                foreach (object e in elems)
                {
                    try
                    {
                        if (e == null) continue;
                        keys.Add(new RawKey
                        {
                            Time = ToFloat(GetFieldOrProp(e, "time", "Time"), 0f),
                            Value = ToFloat(GetFieldOrProp(e, "value", "Value"), 0f),
                            InT = ToFloat(GetFieldOrProp(e, "inTangent", "InTangent"), 0f),
                            OutT = ToFloat(GetFieldOrProp(e, "outTangent", "OutTangent"), 0f),
                        });
                    }
                    catch { }
                }
                return keys.Count > 0 ? keys.ToArray() : null;
#else
                return null;
#endif
            }
            catch { return null; }
        }

        /// <summary>
        /// 将单个 AnimationClip 反向转换为 motion3 曲线数组。
        /// </summary>
        private static Motion3Curve[] ConvertClipToMotion3Curves(object clipObj, Dictionary<string, KeyValuePair<string, string>> paramMap, out float duration, out float fps, out bool loop)
        {
            duration = 0f; fps = 30f; loop = true;

            duration = ToFloat(GetFieldOrProp(clipObj, "length", "Length"), 0f);
            fps = ToFloat(GetFieldOrProp(clipObj, "frameRate", "FrameRate"), 30f);
            if (fps <= 0f) fps = 30f;
            loop = ToBool(GetFieldOrProp(clipObj, "isLooping", "IsLooping", "loopTime"), true);

            List<object> bindings = GetCurveBindingsReflective(clipObj);
            if (bindings == null || bindings.Count == 0)
            {
                if (!motion3DiagLogged)
                {
                    motion3DiagLogged = true;
                    ExplorerCore.LogWarning("[Live2D] clip 曲线绑定为空（GetCurveBindings 返回 null/空）");
                }
                return null;
            }

            List<Motion3Curve> curves = new();
            float maxEndTime = 0f;

            foreach (object binding in bindings)
            {
                try
                {
                    if (binding == null) continue;
                    ReadBindingInfo(binding, out string path, out string typeName, out string propName);

                    // 一次性诊断：首个绑定的信息 + GetEditorCurve 结果
                    if (!motion3DiagLogged)
                    {
                        motion3DiagLogged = true;
                        UnityEngine.AnimationCurve diagCurve = GetEditorCurveReflective(clipObj, binding);
                        ExplorerCore.LogWarning($"[Live2D] 绑定数 {bindings.Count}, 首个: path='{path}' type='{typeName}' prop='{propName}', GetEditorCurve={(diagCurve != null ? "OK" : "null")}");
                    }

                    string target, id;
                    MapCurveTargetId(typeName, propName, path, paramMap, out target, out id);

                    UnityEngine.AnimationCurve curve = GetEditorCurveReflective(clipObj, binding);
                    if (curve == null) continue;

                    RawKey[] keys = KeysToRaw(curve.keys);
                    if (keys == null || keys.Length == 0) continue;

                    Motion3Curve mc = new Motion3Curve();
                    mc.Target = target;
                    mc.Id = id;
                    if (EncodeCurveSegments(keys, mc.Segments, out int segCount))
                    {
                        mc.SegmentCount = segCount;
                        curves.Add(mc);
                        if (keys.Length > 0 && keys[keys.Length - 1].Time > maxEndTime)
                            maxEndTime = keys[keys.Length - 1].Time;
                    }
                }
                catch { }
            }

            if (curves.Count == 0)
                return null;

            if (duration <= 0f && maxEndTime > 0f)
                duration = maxEndTime;

            return curves.ToArray();
        }

        /// <summary>
        /// 从已加载程序集中按全名查找类型（net35 编译门面缺 AnimationUtility 等类型时用）。
        /// </summary>
        private static System.Type FindLoadedType(string fullName)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        System.Type t = asm.GetType(fullName, false);
                        if (t != null) return t;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 反射调用 AnimationClip.GetCurveBindings()（internal），返回绑定列表。
        /// CPP(IL2CPP)：Il2Cpp 反射；Mono：System 反射。
        /// </summary>
        private static List<object> GetCurveBindingsReflective(object clipObj)
        {
            try
            {
                object result = null;
#if CPP
                Il2CppSystem.Object iobj = clipObj.TryCast<Il2CppSystem.Object>();
                if (iobj != null)
                {
                    // AnimationUtility.GetCurveBindings(AnimationClip) 是 AnimationUtility 的静态方法，
                    // 不是 AnimationClip 的实例方法！（之前在 clipType 上找永远找不到）
                    Il2CppSystem.Type utilType = Il2CppSystem.Type.GetType("UnityEngine.AnimationUtility, UnityEngine.AnimationModule");
                    if (utilType == null)
                        utilType = Il2CppSystem.Type.GetType("UnityEngine.AnimationUtility, UnityEngine.CoreModule");
                    if (utilType != null)
                    {
                        var flags = Il2CppSystem.Reflection.BindingFlags.Static
                            | Il2CppSystem.Reflection.BindingFlags.Public
                            | Il2CppSystem.Reflection.BindingFlags.NonPublic;
                        var methods = utilType.GetMethods(flags);
                        if (methods != null)
                        {
                            foreach (var m in methods)
                            {
                                if (m.Name != "GetCurveBindings") continue;
                                var ps = m.GetParameters();
                                if (ps.Length != 1) continue;
                                try
                                {
                                    result = m.Invoke(null, new Il2CppSystem.Object[] { iobj });
                                }
                                catch { }
                                if (result != null) break;
                            }
                        }
                    }
                }
#else
                // net35 编译门面没有 AnimationUtility 类型，运行时从已加载程序集查找
                System.Type utilType = FindLoadedType("UnityEngine.AnimationUtility");
                System.Reflection.MethodInfo m = utilType != null ? utilType.GetMethod("GetCurveBindings",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) : null;
                if (m != null)
                    result = m.Invoke(null, new object[] { clipObj });
#endif
                if (result != null)
                    return EnumerateObjects(result);

                // 一次性诊断：静态反射调用失败（可能被 IL2CPP 剥离）
                if (!curveBindingDiagLogged)
                {
                    curveBindingDiagLogged = true;
                    ExplorerCore.LogWarning("[Live2D] AnimationUtility.GetCurveBindings 静态反射调用失败（可能被剥离或参数不匹配）");
                }
            }
            catch { }
            return null;
        }

        private static bool curveBindingDiagLogged = false;
        private static bool motion3DiagLogged = false;

        /// <summary>
        /// 读取 EditorCurveBinding 的 path/typeName/propertyName。
        /// </summary>
        private static void ReadBindingInfo(object binding, out string path, out string typeName, out string propName)
        {
            path = ""; typeName = ""; propName = "";
            try
            {
#if CPP
                Il2CppSystem.Object bobj = binding.TryCast<Il2CppSystem.Object>();
                if (bobj != null)
                {
                    Il2CppSystem.Type bt = bobj.GetIl2CppType();
                    if (bt != null)
                    {
                        path = ReadIl2CppStringField(bt, bobj, "path");
                        propName = ReadIl2CppStringField(bt, bobj, "propertyName");
                        object typeVal = ReadIl2CppField(bt, bobj, "type");
                        if (typeVal != null)
                        {
                            Il2CppSystem.Type tt = typeVal.TryCast<Il2CppSystem.Type>();
                            if (tt != null) typeName = tt.FullName ?? tt.Name ?? "";
                        }
                    }
                }
#else
                System.Type t = binding.GetType();
                path = ReadFieldValue(t, binding, "path") as string ?? "";
                propName = ReadFieldValue(t, binding, "propertyName") as string ?? "";
                object tv = ReadFieldValue(t, binding, "type");
                if (tv is System.Type st) typeName = st.FullName ?? st.Name;
#endif
            }
            catch { }
        }

        /// <summary>
        /// 反射调用 AnimationClip.GetEditorCurve(EditorCurveBinding)（internal），返回 AnimationCurve。
        /// </summary>
        private static UnityEngine.AnimationCurve GetEditorCurveReflective(object clipObj, object binding)
        {
            try
            {
#if CPP
                Il2CppSystem.Object iobj = clipObj.TryCast<Il2CppSystem.Object>();
                Il2CppSystem.Object bobj = binding.TryCast<Il2CppSystem.Object>();
                if (iobj != null && bobj != null)
                {
                    // AnimationUtility.GetEditorCurve(AnimationClip, EditorCurveBinding) 是 AnimationUtility
                    // 的静态方法，不是 AnimationClip 的实例方法！
                    Il2CppSystem.Type utilType = Il2CppSystem.Type.GetType("UnityEngine.AnimationUtility, UnityEngine.AnimationModule");
                    if (utilType == null)
                        utilType = Il2CppSystem.Type.GetType("UnityEngine.AnimationUtility, UnityEngine.CoreModule");
                    if (utilType != null)
                    {
                        var flags = Il2CppSystem.Reflection.BindingFlags.Static
                            | Il2CppSystem.Reflection.BindingFlags.Public
                            | Il2CppSystem.Reflection.BindingFlags.NonPublic;
                        var methods = utilType.GetMethods(flags);
                        if (methods != null)
                        {
                            foreach (var m in methods)
                            {
                                if (m.Name != "GetEditorCurve") continue;
                                var ps = m.GetParameters();
                                if (ps.Length != 2) continue;
                                try
                                {
                                    object curveObj = m.Invoke(null, new Il2CppSystem.Object[] { iobj, bobj });
                                    if (curveObj != null)
                                    {
                                        var curve = curveObj.TryCast<UnityEngine.AnimationCurve>();
                                        if (curve != null) return curve;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
#else
                // net35 编译门面没有 AnimationUtility 类型，运行时从已加载程序集查找
                System.Type utilType = FindLoadedType("UnityEngine.AnimationUtility");
                System.Reflection.MethodInfo m = utilType != null ? utilType.GetMethod("GetEditorCurve",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) : null;
                if (m != null)
                {
                    object curveObj = m.Invoke(null, new object[] { clipObj, binding });
                    if (curveObj is UnityEngine.AnimationCurve curve) return curve;
                }
#endif
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 曲线目标/ID 映射：控制器 → Model/Opacity|EyeBlink|LipSync；
        /// 否则按路径末段匹配 CubismParameter/CubismPart 的 Id 或 gameObject 名。
        /// </summary>
        private static void MapCurveTargetId(string typeName, string propName, string path, Dictionary<string, KeyValuePair<string, string>> paramMap, out string target, out string id)
        {
            // 控制器特殊映射（绑定类型即控制器类型）
            if (typeName.Contains("CubismRenderController")) { target = "Model"; id = "Opacity"; return; }
            if (typeName.Contains("CubismEyeBlinkController")) { target = "Model"; id = "EyeBlink"; return; }
            if (typeName.Contains("CubismMouthController")) { target = "Model"; id = "LipSync"; return; }

            string leaf = path;
            int slash = path.LastIndexOf('/');
            if (slash >= 0 && slash < path.Length - 1)
                leaf = path.Substring(slash + 1);

            if (paramMap != null && !string.IsNullOrEmpty(leaf) && paramMap.TryGetValue(leaf, out var mapped))
            {
                target = mapped.Key;
                id = mapped.Value;
                return;
            }

            target = "Parameter";
            id = string.IsNullOrEmpty(propName) ? leaf : propName;
        }

        /// <summary>
        /// 构建 CubismParameter/CubismPart 的索引（Id 与 gameObject 名 → (Target, Id)）。
        /// </summary>
        private static Dictionary<string, KeyValuePair<string, string>> BuildCubismParamMap(GameObject root)
        {
            Dictionary<string, KeyValuePair<string, string>> map = new(StringComparer.OrdinalIgnoreCase);
            if (root == null) return map;
            BuildCubismParamMapRecursive(root.transform, map, 0);
            return map;
        }

        private static void BuildCubismParamMapRecursive(Transform t, Dictionary<string, KeyValuePair<string, string>> map, int depth)
        {
            if (t == null || depth > 20) return;
            try
            {
                Component[] comps = t.gameObject.GetComponents<Component>();
                if (comps != null)
                {
                    foreach (Component comp in comps)
                    {
                        try
                        {
                            if (comp == null) continue;
                            string tn = GetComponentTypeName(comp);
                            string target = null;
                            if (tn.Contains("CubismPart")) target = "PartOpacity";
                            else if (tn.Contains("CubismParameter")) target = "Parameter";
                            if (target == null) continue;

                            string id = SafeString(GetFieldOrProp(comp, "Id", "id"), null);
                            string goName = comp.gameObject.name;
                            string effectiveId = string.IsNullOrEmpty(id) ? goName : id;

                            if (!string.IsNullOrEmpty(effectiveId) && !map.ContainsKey(effectiveId))
                                map[effectiveId] = new KeyValuePair<string, string>(target, effectiveId);
                            if (!string.IsNullOrEmpty(goName) && !map.ContainsKey(goName))
                                map[goName] = new KeyValuePair<string, string>(target, effectiveId);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            for (int i = 0; i < t.childCount; i++)
            {
                try { BuildCubismParamMapRecursive(t.GetChild(i), map, depth + 1); } catch { }
            }
        }

        /// <summary>
        /// 按官方 Segments 编码把 RawKey[] 转成扁平 float 数组：
        /// 起始点 [t,v]；段：type 0=linear [0,t,v]；type 1=bezier [1,cp1t,cp1v,cp2t,cp2v,t,v]；
        /// type 2=stepped / 3=inverseStepped [2|3,t,v]。
        /// </summary>
        private static bool EncodeCurveSegments(RawKey[] keys, List<float> segs, out int segmentCount)
        {
            segmentCount = 0;
            if (keys == null || keys.Length == 0)
                return false;

            segs.Add(keys[0].Time);
            segs.Add(keys[0].Value);

            for (int i = 1; i < keys.Length; i++)
            {
                RawKey k0 = keys[i - 1];
                RawKey k1 = keys[i];
                float dt = k1.Time - k0.Time;
                if (dt <= 0f)
                    continue;

                float linearSlope = (k1.Value - k0.Value) / dt;

                bool stepped = float.IsPositiveInfinity(k0.OutT) || float.IsPositiveInfinity(k1.InT)
                    || float.IsNegativeInfinity(k0.OutT) || float.IsNegativeInfinity(k1.InT);

                if (stepped)
                {
                    segs.Add(2f);
                    segs.Add(k1.Time);
                    segs.Add(k1.Value);
                    segmentCount++;
                    continue;
                }

                bool linear = NearlyEqual(k0.OutT, linearSlope) && NearlyEqual(k1.InT, linearSlope);
                if (linear)
                {
                    segs.Add(0f);
                    segs.Add(k1.Time);
                    segs.Add(k1.Value);
                    segmentCount++;
                }
                else
                {
                    float cp1t = k0.Time + dt / 3f;
                    float cp1v = k0.Value + k0.OutT * dt / 3f;
                    float cp2t = k1.Time - dt / 3f;
                    float cp2v = k1.Value - k1.InT * dt / 3f;
                    segs.Add(1f);
                    segs.Add(cp1t);
                    segs.Add(cp1v);
                    segs.Add(cp2t);
                    segs.Add(cp2v);
                    segs.Add(k1.Time);
                    segs.Add(k1.Value);
                    segmentCount++;
                }
            }
            return segmentCount > 0;
        }

        private static bool NearlyEqual(float a, float b)
        {
            if (float.IsInfinity(a) || float.IsInfinity(b) || float.IsNaN(a) || float.IsNaN(b))
                return a == b;
            return System.Math.Abs(a - b) < 1e-4f;
        }

        /// <summary>
        /// 序列化 motion3.json（Version + Meta + Curves + UserData）。
        /// </summary>
        private static string SerializeMotion3Json(Motion3Curve[] curves, float duration, float fps, bool loop)
        {
            try
            {
                int totalSegments = 0, totalPoints = 0;
                foreach (var c in curves)
                {
                    totalSegments += c.SegmentCount;
                    totalPoints += 1; // 起始点
                    totalPoints += c.SegmentCount; // 每段至少 1 个端点
                    totalPoints += CountBezierSegments(c.Segments) * 2; // bezier 段额外 2 个控制点
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"Version\": 3,");
                sb.AppendLine("  \"Meta\": {");
                sb.AppendLine("    \"Duration\": " + Fmt(duration) + ",");
                sb.AppendLine("    \"Fps\": " + Fmt(fps) + ",");
                sb.AppendLine("    \"Loop\": " + (loop ? "true" : "false") + ",");
                sb.AppendLine("    \"AreBeziersRestricted\": true,");
                sb.AppendLine("    \"CurveCount\": " + curves.Length + ",");
                sb.AppendLine("    \"TotalSegmentCount\": " + totalSegments + ",");
                sb.AppendLine("    \"TotalPointCount\": " + totalPoints);
                sb.AppendLine("  },");
                sb.AppendLine("  \"Curves\": [");
                for (int i = 0; i < curves.Length; i++)
                {
                    var c = curves[i];
                    sb.AppendLine("    {");
                    sb.AppendLine("      \"Target\": \"" + EscapeJson(c.Target) + "\",");
                    sb.AppendLine("      \"Id\": \"" + EscapeJson(c.Id) + "\",");
                    sb.Append("      \"Segments\": [");
                    for (int j = 0; j < c.Segments.Count; j++)
                    {
                        if (j > 0) sb.Append(", ");
                        sb.Append(Fmt(c.Segments[j]));
                    }
                    sb.AppendLine("]");
                    sb.AppendLine(i < curves.Length - 1 ? "    }," : "    }");
                }
                sb.AppendLine("  ],");
                sb.AppendLine("  \"UserData\": {}");
                sb.AppendLine("}");
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static int CountBezierSegments(List<float> segs)
        {
            int count = 0;
            int idx = 2; // 跳过起始点
            while (idx < segs.Count)
            {
                float type = segs[idx];
                if (type == 1f) { count++; idx += 7; }
                else if (type == 0f || type == 2f || type == 3f) { idx += 3; }
                else idx++;
            }
            return count;
        }

        private static string Fmt(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return "0";
            return f.ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string SafeString(object v, string fallback)
        {
            if (v == null) return fallback;
            string s = v.ToString();
            return string.IsNullOrEmpty(s) ? fallback : s;
        }

        private static float ToFloat(object v, float fallback)
        {
            try
            {
                if (v == null) return fallback;
                if (v is float f) return f;
                return System.Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return fallback; }
        }

        private static bool ToBool(object v, bool fallback)
        {
            try
            {
                if (v == null) return fallback;
                if (v is bool b) return b;
                string s = v.ToString().ToLower();
                if (s == "true" || s == "1") return true;
                if (s == "false" || s == "0") return false;
            }
            catch { }
            return fallback;
        }

        private static Component FindComponentByTypeName(GameObject root, string typeKeyword)
        {
            if (root == null) return null;
            return FindComponentByTypeNameRecursive(root.transform, typeKeyword, 0);
        }

        private static Component FindComponentByTypeNameRecursive(Transform t, string kw, int depth)
        {
            if (t == null || depth > 20) return null;
            try
            {
                Component[] comps = t.gameObject.GetComponents<Component>();
                if (comps != null)
                {
                    foreach (Component c in comps)
                    {
                        if (c == null) continue;
                        if (GetComponentTypeName(c).Contains(kw))
                            return c;
                    }
                }
            }
            catch { }
            for (int i = 0; i < t.childCount; i++)
            {
                try
                {
                    var r = FindComponentByTypeNameRecursive(t.GetChild(i), kw, depth + 1);
                    if (r != null) return r;
                }
                catch { }
            }
            return null;
        }

#if CPP
        private static object ReadIl2CppField(Il2CppSystem.Type t, Il2CppSystem.Object obj, string name)
        {
            try
            {
                var flags = Il2CppSystem.Reflection.BindingFlags.Instance
                    | Il2CppSystem.Reflection.BindingFlags.Public
                    | Il2CppSystem.Reflection.BindingFlags.NonPublic;
                var f = t.GetField(name, flags);
                if (f != null) return f.GetValue(obj);
            }
            catch { }
            return null;
        }

        private static string ReadIl2CppStringField(Il2CppSystem.Type t, Il2CppSystem.Object obj, string name)
        {
            object v = ReadIl2CppField(t, obj, name);
            return v != null ? v.ToString() : "";
        }
#else
        private static object ReadFieldValue(System.Type t, object obj, string name)
        {
            try
            {
                System.Reflection.FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f.GetValue(obj);
            }
            catch { }
            return null;
        }
#endif

        #endregion

        /// <summary>
        /// 扫描组件中所有 TextAsset 类型字段并导出（Cubism 控制器上的 physics3/pose3/motion3/exp3 json）。
        /// </summary>
        private static void CollectTextAssetFields(Component comp, List<ExportableItem> items, string objPath)
        {
            if (textAssetScanCount > MaxTextAssetFieldScans)
                return;

            try
            {
                List<KeyValuePair<string, object>> fieldValues = GetAllFieldValues(comp);
                textAssetScanCount++;
                if (fieldValues == null) return;

                foreach (var kv in fieldValues)
                {
                    try
                    {
                        object v = kv.Value;
                        if (v == null) continue;
                        TextAsset ta = v.TryCast<TextAsset>();
                        if (ta == null) continue;

                        string taName = string.IsNullOrEmpty(ta.name) ? objPath + "_" + kv.Key : ta.name;
                        AddTextAssetExportItem(items, ta, taName, "Live2D Data");
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 添加 TextAsset 导出项：立即读取字节缓存（防止导出时对象被销毁），
        /// 读取失败则导出时再尝试读一次。
        /// </summary>
        private static void AddTextAssetExportItem(List<ExportableItem> items, TextAsset ta, string name, string typePrefix)
        {
            try
            {
                if (ta == null) return;
                byte[] bytes = ReadTextAssetFull(ta);
                string ext = bytes != null ? DetectBytesExtension(bytes, name) : DetectTextAssetExtension(ta);

                string key = name + "_" + ext;
                if (exportedNames.Contains(key))
                    return;
                exportedNames.Add(key);

                if (bytes != null)
                {
                    byte[] captured = bytes;
                    string capturedExt = ext;
                    items.Add(new ExportableItem
                    {
                        Name = name,
                        Type = typePrefix + " (." + capturedExt + ")",
                        Asset = ta,
                        ExportAction = (path) => ExportBytes(captured, path, capturedExt)
                    });
                }
                else
                {
                    // 数据暂不可读：导出时再尝试（可能资源已加载）
                    TextAsset capturedTA = ta;
                    string lazyExt = ext;
                    items.Add(new ExportableItem
                    {
                        Name = name,
                        Type = typePrefix + " (." + lazyExt + ")",
                        Asset = capturedTA,
                        ExportAction = (path) => ExportTextAsset(capturedTA, path, lazyExt)
                    });
                }
            }
            catch { }
        }

        #endregion

        #region Spine 导出（基于 spine-unity 4.x 官方源码字段链）

        /// <summary>
        /// 导出 Spine 骨骼模型：skeleton json/skel + atlas.txt + 贴图 png。
        /// 字段链（spine-unity 4.x 官方源码）：
        ///   SkeletonRenderer.skeletonDataAsset → SkeletonDataAsset
        ///   SkeletonDataAsset.skeletonJSON (TextAsset) + atlasAssets (AtlasAssetBase[])
        ///   SpineAtlasAsset.atlasFile (TextAsset) + materials (Material[])
        /// </summary>
        private static void ExportSpineModel(Component comp, List<ExportableItem> items, string objPath)
        {
            try
            {
                if (comp == null) return;
                if (!processedSpineComps.Add(comp.GetInstanceID()))
                    return;

                string compName = GetComponentTypeName(comp);
                string skelName = string.IsNullOrEmpty(comp.gameObject.name) ? "spine_skeleton" : comp.gameObject.name;
                ExplorerCore.Log($"[Spine] 检测到 Spine 组件: {compName} ({skelName})");

                // 1. skeletonDataAsset
                object sda = GetFieldOrProp(comp, "skeletonDataAsset", "_skeletonDataAsset", "SkeletonDataAsset", "skeletonDataAsset");
                if (sda == null)
                {
                    ExplorerCore.LogWarning($"[Spine] '{skelName}' 未取到 skeletonDataAsset 字段，输出组件字段诊断");
                    DumpComponentValuesOnce(comp, compName);
                    return;
                }

                // 2. skeletonJSON（TextAsset：.json 或 .skel.bytes）
                try
                {
                    object sj = GetFieldOrProp(sda, "skeletonJSON", "_skeletonJSON", "skeletonJson");
                    TextAsset sjTA = sj != null ? sj.TryCast<TextAsset>() : null;
                    if (sjTA != null)
                    {
                        byte[] sjBytes = ReadTextAssetFull(sjTA);
                        string sjName = string.IsNullOrEmpty(sjTA.name) ? skelName + "_skeleton" : sjTA.name;
                        // 内容判断：'{' 开头 → json；否则按二进制 .skel 处理
                        string ext = "skel.bytes";
                        if (sjBytes != null && sjBytes.Length > 0 && sjBytes[0] == 0x7B)
                            ext = "json";
                        else if (sjName.ToLower().Contains(".json"))
                            ext = "json";

                        string key = sjName + "_spine_" + ext;
                        if (!exportedNames.Contains(key) && (sjBytes != null || sjTA != null))
                        {
                            exportedNames.Add(key);
                            if (sjBytes != null)
                            {
                                byte[] captured = sjBytes;
                                string capturedExt = ext;
                                items.Add(new ExportableItem
                                {
                                    Name = sjName,
                                    Type = "Spine Skeleton (." + capturedExt + ")",
                                    Asset = sjTA,
                                    ExportAction = (path) => ExportBytes(captured, path, capturedExt)
                                });
                            }
                            else
                            {
                                TextAsset capturedTA = sjTA;
                                string lazyExt = ext;
                                items.Add(new ExportableItem
                                {
                                    Name = sjName,
                                    Type = "Spine Skeleton (." + lazyExt + ")",
                                    Asset = capturedTA,
                                    ExportAction = (path) => ExportTextAsset(capturedTA, path, lazyExt)
                                });
                            }
                        }
                    }
                    else
                    {
                        ExplorerCore.LogWarning($"[Spine] '{skelName}' 的 SkeletonDataAsset 上未取到 skeletonJSON");
                    }
                }
                catch (System.Exception ex)
                {
                    ExplorerCore.LogWarning($"[Spine] skeletonJSON 导出异常: {ex.Message}");
                }

                // 3. atlasAssets[] → atlasFile + materials 贴图
                try
                {
                    object atlasAssets = GetFieldOrProp(sda, "atlasAssets", "_atlasAssets");
                    foreach (object atlasAsset in EnumerateObjects(atlasAssets))
                    {
                        try
                        {
                            if (atlasAsset == null) continue;

                            // v16u：解析 atlas.txt 里每个图集页的期望尺寸（页名 → 宽高）
                            //   游戏运行时贴图常被压到 2048 上限，而 atlas.txt 记录的是原始打包尺寸，
                            //   导出的 PNG 若不拉伸回去，配 .json + .atlas.txt 用时 region bounds 全对不上。
                            List<SpineAtlasPage> spinePages = null;

                            // atlas.txt
                            object af = GetFieldOrProp(atlasAsset, "atlasFile", "_atlasFile");
                            TextAsset atlasTA = af != null ? af.TryCast<TextAsset>() : null;
                            if (atlasTA != null)
                            {
                                string aname = string.IsNullOrEmpty(atlasTA.name) ? skelName + "_atlas" : atlasTA.name;
                                AddTextAssetExportItem(items, atlasTA, aname, "Spine Atlas");
                                // atlas 后缀修正为 .atlas.txt 由导出时处理（DetectBytesExtension 对文本返回 txt，这里直接用 atlas.txt）

                                spinePages = ParseSpineAtlasPageSizes(atlasTA);
                                if (spinePages != null && spinePages.Count > 0)
                                    ExplorerCore.Log($"[Spine] '{aname}' 记录了 {spinePages.Count} 个图集页尺寸，贴图导出时将按页尺寸对齐");
                            }

                            // 贴图：materials[] → mainTexture
                            //   v16u：先收集本图集全部贴图，再统一按 atlas.txt 页名（数量一致时按顺序）匹配目标尺寸
                            object mats = GetFieldOrProp(atlasAsset, "materials", "_materials", "Materials");
                            List<Texture2D> pageTextures = new List<Texture2D>();
                            List<int> pageTextureIds = new List<int>();
                            foreach (object m in EnumerateObjects(mats))
                            {
                                try
                                {
                                    Material mat = m != null ? m.TryCast<Material>() : null;
                                    if (mat == null) continue;
                                    Texture mt = mat.mainTexture;
                                    if (mt == null && mat.HasProperty("_MainTex"))
                                        mt = mat.GetTexture("_MainTex");
                                    if (mt != null)
                                    {
                                        Texture2D tex = mt.TryCast<Texture2D>();
                                        if (tex != null)
                                        {
                                            int tid = tex.GetInstanceID();
                                            if (!pageTextureIds.Contains(tid))
                                            {
                                                pageTextureIds.Add(tid);
                                                pageTextures.Add(tex);
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }

                            // 兜底：PrimaryMaterial
                            if (pageTextures.Count == 0)
                            {
                                object pm = GetFieldOrProp(atlasAsset, "PrimaryMaterial", "_primaryMaterial");
                                Material pmat = pm != null ? pm.TryCast<Material>() : null;
                                if (pmat != null)
                                {
                                    Texture mt = pmat.mainTexture;
                                    if (mt != null)
                                    {
                                        Texture2D tex = mt.TryCast<Texture2D>();
                                        if (tex != null)
                                            pageTextures.Add(tex);
                                    }
                                }
                            }

                            AddSpineTexturesWithAtlasSizes(pageTextures, items, spinePages);
                        }
                        catch { }
                    }
                }
                catch (System.Exception ex)
                {
                    ExplorerCore.LogWarning($"[Spine] atlas 导出异常: {ex.Message}");
                }
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"[Spine] Spine 导出失败: {ex.Message}");
            }
        }

        // ---- v16u：Spine 图集页尺寸对齐 ----

        /// <summary>atlas.txt 里的一个图集页（页名 + 期望宽高）。</summary>
        private class SpineAtlasPage
        {
            public string Name;
            public int Width;
            public int Height;
        }

        /// <summary>
        /// v16u：解析 atlas.txt 文本中每个图集页的尺寸。
        /// atlas.txt 格式：页名行，下一行 "size:宽,高"，再 filter/pma，然后是 region 块。
        /// </summary>
        private static List<SpineAtlasPage> ParseSpineAtlasPageSizes(TextAsset atlasTA)
        {
            try
            {
                if (atlasTA == null) return null;
                byte[] bytes = ReadTextAssetFull(atlasTA);
                if (bytes == null || bytes.Length == 0) return null;
                // 去 BOM（Unity TextAsset 可能带 EF BB BF 头）
                string text = Encoding.UTF8.GetString(bytes);
                if (text.StartsWith("\uFEFF", StringComparison.Ordinal))
                    text = text.Substring(1);

                string[] lines = text.Split('\n');
                List<SpineAtlasPage> pages = new List<SpineAtlasPage>();
                for (int i = 0; i < lines.Length - 1; i++)
                {
                    string pageName = lines[i].Trim();
                    string next = lines[i + 1].Trim();
                    if (pageName.Length == 0 || !next.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // size:W,H（可能有空格）
                    string sizePart = next.Substring(5).Trim();
                    string[] wh = sizePart.Split(',');
                    int w, h;
                    if (wh.Length == 2
                        && int.TryParse(wh[0].Trim(), out w)
                        && int.TryParse(wh[1].Trim(), out h)
                        && w > 0 && h > 0)
                    {
                        pages.Add(new SpineAtlasPage { Name = pageName, Width = w, Height = h });
                        i++; // 页头 2 行消费掉，跳过 size 行
                    }
                }
                return pages;
            }
            catch { return null; }
        }

        /// <summary>
        /// v16u：按贴图名匹配 atlas 页（页名去目录、去扩展名后与贴图名忽略大小写比对）。
        /// </summary>
        private static SpineAtlasPage FindPageByName(List<SpineAtlasPage> pages, string textureName)
        {
            if (pages == null || pages.Count == 0 || string.IsNullOrEmpty(textureName)) return null;
            foreach (SpineAtlasPage p in pages)
            {
                if (p == null || string.IsNullOrEmpty(p.Name)) continue;
                string pageKey = p.Name.Trim();
                int slash = pageKey.LastIndexOfAny(new char[] { '/', '\\' });
                if (slash >= 0) pageKey = pageKey.Substring(slash + 1);
                int dot = pageKey.LastIndexOf('.');
                if (dot > 0) pageKey = pageKey.Substring(0, dot);
                if (string.Equals(pageKey, textureName, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        /// <summary>
        /// v16u：把 Spine 图集贴图加入导出列表，尺寸与 atlas.txt 页记录不一致时导出为重采样版本。
        /// 匹配策略：页名 ↔ 贴图名（忽略大小写）；名称对不上但贴图数与页数一致时按顺序对应。
        /// 注意：通用扫描（MeshRenderer → "Texture (.png)"）可能已把同一贴图收进 items，
        ///   需要重采样时先把旧项移除再顶替，否则全局去重会让本方法永远不生效。
        /// </summary>
        private static void AddSpineTexturesWithAtlasSizes(List<Texture2D> textures, List<ExportableItem> items, List<SpineAtlasPage> pages)
        {
            if (textures == null) return;
            if (pages == null || pages.Count == 0)
            {
                for (int i = 0; i < textures.Count; i++)
                    AddTextureItem(textures[i], items, "Spine Texture (.png)");
                return;
            }

            bool orderFallback = textures.Count == pages.Count;
            for (int i = 0; i < textures.Count; i++)
            {
                Texture2D tex = textures[i];
                if (tex == null) continue;

                SpineAtlasPage target = FindPageByName(pages, tex.name);
                if (target == null && orderFallback && i < pages.Count)
                    target = pages[i]; // 名称没对上但数量一致 → 顺序对应（材质顺序通常与页顺序一致）

                if (target != null && (tex.width != target.Width || tex.height != target.Height))
                {
                    AddSpineTextureItem(tex, items, target.Width, target.Height);
                }
                else
                {
                    AddTextureItem(tex, items, "Spine Texture (.png)");
                }
            }
        }

        /// <summary>
        /// v16u：添加"按 atlas.txt 目标尺寸重采样"的 Spine 贴图导出项。
        /// 与 AddTextureItem 的区别：
        ///   1. ExportAction 调 ExportSpineTextureAsPNG（先重采样到目标尺寸再编码）；
        ///   2. 若通用扫描已把该贴图以普通 "Texture (.png)" 收进 items，先移除旧项再顶替
        ///      （IL2CPP 下代理对象实例不唯一，用 GetInstanceID 比对而不是 ReferenceEquals）。
        /// </summary>
        private static void AddSpineTextureItem(Texture2D tex, List<ExportableItem> items, int targetW, int targetH)
        {
            try
            {
                if (tex == null) return;

                // 移除此前通用扫描加入的同贴图普通导出项（在列表里倒序找，避免跳项）
                int texId = tex.GetInstanceID();
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    ExportableItem e = items[i];
                    if (e == null || e.Asset == null) continue;
                    try
                    {
                        if (e.Asset.GetInstanceID() == texId)
                            items.RemoveAt(i);
                    }
                    catch { }
                }

                // 自身去重（本方法对同一贴图可能被多个 atlasAsset 重复触发）
                if (spineResizeAddedIds.Contains(texId)) return;
                spineResizeAddedIds.Add(texId);
                collectedTexIds.Add(texId); // 也占住通用去重集合，防止后续再被普通路径加入

                string name = string.IsNullOrEmpty(tex.name) ? "texture_" + texId : tex.name;
                Texture2D captured = tex;
                int tw = targetW, th = targetH;
                ExplorerCore.Log($"[Spine] 贴图 '{name}' 为 {tex.width}x{tex.height}，atlas.txt 记录 {tw}x{th}，导出时将重采样对齐");
                items.Add(new ExportableItem
                {
                    Name = name,
                    Type = "Spine Texture (" + tex.width + "x" + tex.height + "→" + tw + "x" + th + ")",
                    Asset = captured,
                    ExportAction = (path) => ExportSpineTextureAsPNG(captured, path, tw, th)
                });
            }
            catch { }
        }

        /// <summary>
        /// v16u：导出 Spine 图集页贴图；与 atlas.txt 页尺寸不一致时先重采样到目标尺寸再写 PNG。
        /// 像素来源：可读贴图直接 GetPixels；不可读/压缩贴图 Blit 到 RenderTexture 再 ReadPixels。
        /// PNG 编码复用现有链路（EncodeToPNG 优先，托管编码器兜底）。
        /// </summary>
        private static void ExportSpineTextureAsPNG(Texture2D texture, string path, int targetW, int targetH)
        {
            if (texture == null)
            {
                ExplorerCore.LogWarning("纹理为空，无法导出！");
                return;
            }
            if (targetW <= 0 || targetH <= 0 || (texture.width == targetW && texture.height == targetH))
            {
                // 尺寸一致（或目标非法）→ 走普通导出
                ExportTextureAsPNG(texture, path);
                return;
            }
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                path += ".png";
            path = EnsureValidPath(path);

            Texture2D temp = null;
            Texture2D outTex = null;
            try
            {
                int w = texture.width, h = texture.height;

                // 1) 源像素读入 RGBA32（v16an：反射构造 + 反射 isReadable）
                temp = CreateTempTexture(w, h);
                bool srcReadable = IsTextureReadableReflection(texture);
                if (srcReadable)
                {
                    temp.SetPixels(texture.GetPixels());
                    temp.Apply();
                }
                else
                {
                    // 不可读/压缩纹理：Blit 到 RenderTexture 再 ReadPixels
                    // v16aq：RT 创建也改走多策略（GetTemporary 内部依赖被剥离的
                    // RenderTextureDescriptor..ctor(int,int,GraphicsFormat,int)，在部分 IL2CPP 游戏直接报 Method not found）
                    string rtHow;
                    RenderTexture rt = CreateTempRenderTexture(w, h, out rtHow);
                    RenderTexture prevActive = RenderTexture.active;
                    try
                    {
                        Graphics.Blit(texture, rt);
                        RenderTexture.active = rt;
                        temp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                        temp.Apply();
                    }
                    finally
                    {
                        RenderTexture.active = prevActive;
                        ReleaseTempRenderTexture(rt, rtHow);
                    }
                }

                // 2) 双线性重采样到 atlas.txt 目标尺寸
                Color32[] srcPx = temp.GetPixels32();
                Color32[] dstPx = ResizeBilinearColor32(srcPx, w, h, targetW, targetH);

                outTex = CreateTempTexture(targetW, targetH);
                outTex.SetPixels32(dstPx);
                outTex.Apply(false);

                // 3) PNG 编码（EncodeToPNG 优先，托管编码器兜底 —— 与 ExportTextureViaReadPixels 同链路）
                byte[] bytes = null;
                try { bytes = EncodeToPNG(outTex); } catch { }
                if (bytes == null || bytes.Length == 0)
                {
                    byte[] rgba = new byte[dstPx.Length * 4];
                    for (int i = 0; i < dstPx.Length; i++)
                    {
                        rgba[i * 4] = dstPx[i].r;
                        rgba[i * 4 + 1] = dstPx[i].g;
                        rgba[i * 4 + 2] = dstPx[i].b;
                        rgba[i * 4 + 3] = dstPx[i].a;
                    }
                    // GetPixels32 行 0 为底部，PNG 扫描行 0 应为顶部 → 垂直翻转
                    FlipVertically(rgba, targetW, targetH);
                    bytes = EncodePngManaged(targetW, targetH, rgba);
                }
                if (bytes == null || bytes.Length == 0)
                    throw new System.Exception("PNG 编码失败（EncodeToPNG 与托管编码器均失败）");

                File.WriteAllBytes(path, bytes);
                ExplorerCore.Log("Spine 贴图已按 atlas.txt 尺寸重采样导出: " + path + " (" + w + "x" + h + "→" + targetW + "x" + targetH + ")");
            }
            finally
            {
                ReleaseTempTexture(temp);
                ReleaseTempTexture(outTex);
            }
        }

        /// <summary>
        /// v16u：Color32 数组双线性重采样（边缘 clamp；像素中心对齐）。
        /// pma:true 的预乘 alpha 图用双线性插值恰好是正确做法（PMA 就是为了插值不产生黑边）。
        /// </summary>
        private static Color32[] ResizeBilinearColor32(Color32[] src, int sw, int sh, int tw, int th)
        {
            if (src == null || src.Length < sw * sh || tw <= 0 || th <= 0) return src;
            if (sw == tw && sh == th) return src;

            Color32[] dst = new Color32[tw * th];
            float rx = (float)sw / tw;
            float ry = (float)sh / th;
            for (int y = 0; y < th; y++)
            {
                float sy = (y + 0.5f) * ry - 0.5f;
                int y0 = (int)System.Math.Floor(sy);
                if (y0 < 0) y0 = 0; else if (y0 > sh - 1) y0 = sh - 1;
                int y1 = y0 + 1; if (y1 > sh - 1) y1 = sh - 1;
                float fy = sy - y0; if (fy < 0) fy = 0; else if (fy > 1) fy = 1;

                for (int x = 0; x < tw; x++)
                {
                    float sx = (x + 0.5f) * rx - 0.5f;
                    int x0 = (int)System.Math.Floor(sx);
                    if (x0 < 0) x0 = 0; else if (x0 > sw - 1) x0 = sw - 1;
                    int x1 = x0 + 1; if (x1 > sw - 1) x1 = sw - 1;
                    float fx = sx - x0; if (fx < 0) fx = 0; else if (fx > 1) fx = 1;

                    Color32 c00 = src[y0 * sw + x0];
                    Color32 c10 = src[y0 * sw + x1];
                    Color32 c01 = src[y1 * sw + x0];
                    Color32 c11 = src[y1 * sw + x1];

                    float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
                    dst[y * tw + x] = new Color32(
                        (byte)(c00.r * w00 + c10.r * w10 + c01.r * w01 + c11.r * w11 + 0.5f),
                        (byte)(c00.g * w00 + c10.g * w10 + c01.g * w01 + c11.g * w11 + 0.5f),
                        (byte)(c00.b * w00 + c10.b * w10 + c01.b * w01 + c11.b * w11 + 0.5f),
                        (byte)(c00.a * w00 + c10.a * w10 + c01.a * w01 + c11.a * w11 + 0.5f));
                }
            }
            return dst;
        }

        #endregion

        #region 2D/3D 检测

        /// <summary>
        /// 检测游戏是 2D 还是 3D。
        /// 依据：上下文对象（含子对象）的渲染器类型计分：
        ///   SpriteRenderer/Cubism/Live2D/Spine/SkeletonRenderer/SkeletonGraphic → 2D；
        ///   SkinnedMeshRenderer/普通 MeshRenderer → 3D。
        /// 注意：绝不使用 Camera.allCameras —— 该属性在 IL2CPP + CoreCLR 下调用底层
        ///   native 方法 GetAllCamerasImpl 会触发 AccessViolationException（进程级崩溃，
        ///   try/catch 无法拦截），这是本次导出闪退的直接原因。
        /// </summary>
        public static GameDimension DetectGameDimension()
        {
            return DetectGameDimension(null);
        }

        public static GameDimension DetectGameDimension(GameObject context)
        {
            int score2D = 0, score3D = 0;

            // 上下文对象（含子对象）的渲染器类型计分（最多 200 个）
            if (context != null)
            {
                try
                {
                    Renderer[] renderers = context.GetComponentsInChildren<Renderer>(true);
                    if (renderers != null)
                    {
                        int count = 0;
                        foreach (Renderer r in renderers)
                        {
                            try
                            {
                                if (r == null) continue;
                                if (++count > 200) break;
                                string tn = GetComponentTypeName(r);
                                if (tn.Contains("SpriteRenderer"))
                                    score2D += 2;
                                else if (tn.Contains("Cubism") || tn.Contains("Live2D") || tn.Contains("Spine")
                                      || tn.Contains("SkeletonRenderer") || tn.Contains("SkeletonGraphic"))
                                    score2D += 3;
                                else if (tn.Contains("SkinnedMeshRenderer"))
                                    score3D += 2;
                                else
                                    score3D += 1;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            if (score2D == 0 && score3D == 0)
                return GameDimension.Unknown;
            if (score2D > 0 && score3D > 0)
                return GameDimension.Mixed;
            return score2D > 0 ? GameDimension.Game2D : GameDimension.Game3D;
        }

        /// <summary>
        /// 检测选中对象（含子对象）属于哪类资产。
        /// 优先级：Live2D > Spine > 3D > 2D Sprite > 未知。
        /// </summary>
        public static AssetKind DetectAssetKind(GameObject context)
        {
            if (context == null)
                return AssetKind.Unknown;

            try
            {
                // Live2D / Spine 组件优先（一个对象只会是其中一种）
                if (IsLive2DObject(context))
                    return AssetKind.Live2D;
                if (IsSpineObject(context))
                    return AssetKind.Spine;
            }
            catch { }

            try
            {
                GameDimension dim = DetectGameDimension(context);
                if (dim == GameDimension.Game3D)
                    return AssetKind.Model3D;
                if (dim == GameDimension.Game2D)
                    return AssetKind.Sprite2D;
            }
            catch { }

            return AssetKind.Unknown;
        }

        /// <summary>
        /// 按资产类型过滤导出项，实现"各自只导出各自对应的文件"。
        /// Unknown 不过滤；其余类型只保留对应 Type 前缀的条目。
        /// </summary>
        public static void FilterItemsByKind(List<ExportableItem> items, AssetKind kind)
        {
            if (items == null || kind == AssetKind.Unknown)
                return;
            items.RemoveAll(it => !MatchesKind(it, kind));
        }

        private static bool MatchesKind(ExportableItem it, AssetKind kind)
        {
            if (it == null) return false;
            string t = it.Type ?? "";

            switch (kind)
            {
                case AssetKind.Model3D:
                    // 3D：Mesh(.fbx) + 普通贴图 + 音频；排除 Live2D/Spine 专属项
                    return !t.Contains("Live2D") && !t.Contains("Spine");

                case AssetKind.Live2D:
                    // Live2D：moc3/moc + 贴图 + model3.json + motion3.json + physics/pose/exp
                    return t.Contains("Live2D");

                case AssetKind.Spine:
                    // Spine：skeleton(.json/.skel) + 贴图 + atlas
                    return t.Contains("Spine");

                case AssetKind.Sprite2D:
                    // 普通 2D：仅贴图
                    return t.Contains("Texture");

                default:
                    return true;
            }
        }

        /// <summary>
        /// 检测对象（含子对象，限 300 个）是否为 Live2D 模型。
        /// </summary>
        public static bool IsLive2DObject(GameObject go)
        {
            return ContainsComponentType(go, t => t.Contains("Cubism") || t.Contains("Live2D"), 300);
        }

        /// <summary>
        /// 检测对象（含子对象，限 300 个）是否为 Spine 骨骼。
        /// </summary>
        public static bool IsSpineObject(GameObject go)
        {
            return ContainsComponentType(go, t => t.Contains("Spine") || t.Contains("SkeletonRenderer")
                || t.Contains("SkeletonAnimation") || t.Contains("SkeletonMecanim") || t.Contains("SkeletonGraphic"), 300);
        }

        private static bool ContainsComponentType(GameObject go, Func<string, bool> matcher, int maxObjects)
        {
            if (go == null)
                return false;
            int scanned = 0;
            return ContainsComponentTypeRecursive(go.transform, matcher, maxObjects, ref scanned, 0);
        }

        private static bool ContainsComponentTypeRecursive(Transform t, Func<string, bool> matcher, int maxObjects, ref int scanned, int depth)
        {
            if (t == null || depth > 20 || scanned > maxObjects)
                return false;
            scanned++;

            try
            {
                Component[] components = t.gameObject.GetComponents<Component>();
                if (components != null)
                {
                    foreach (Component comp in components)
                    {
                        try
                        {
                            if (comp == null) continue;
                            if (matcher(GetComponentTypeName(comp)))
                                return true;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            for (int i = 0; i < t.childCount; i++)
            {
                try
                {
                    Transform child = t.GetChild(i);
                    if (child != null && ContainsComponentTypeRecursive(child, matcher, maxObjects, ref scanned, depth + 1))
                        return true;
                }
                catch { }
            }
            return false;
        }

        #endregion

        #region IL2CPP / Mono 反射基础设施

        /// <summary>
        /// 获取组件的真实类型名（IL2CPP Interop 下必须用 GetIl2CppType 才能拿到真实类型名）
        /// </summary>
        public static string GetComponentTypeName(Component comp)
        {
            if (comp == null)
                return "";

            try
            {
#if CPP
                // Il2CppInterop/Unhollower: GetType() 对游戏程序集类型可能返回基类"Component"
                var il2cppType = comp.GetIl2CppType();
                if (il2cppType != null)
                {
                    string fn = il2cppType.FullName ?? il2cppType.Name ?? "";
                    if (!string.IsNullOrEmpty(fn))
                        return fn;
                }
                return comp.GetType().Name;
#else
                return comp.GetType().Name;
#endif
            }
            catch
            {
                try { return comp.GetType().Name; }
                catch { return ""; }
            }
        }

        /// <summary>
        /// 按候选名列表读取字段或属性（返回第一个非 null 的值）。
        /// 三级策略：Il2Cpp 反射 → 代理重包装+System 反射 → System 反射（Mono）。
        /// </summary>
        internal static object GetFieldOrProp(object obj, params string[] names)
        {
            if (obj == null || names == null)
                return null;
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                object v = GetFieldOrPropSingle(obj, name);
                if (v != null)
                    return v;
            }
            return null;
        }

        private static object GetFieldOrPropSingle(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name))
                return null;

#if CPP
            try
            {
                Il2CppSystem.Object iobj = obj.TryCast<Il2CppSystem.Object>();
                if (iobj != null)
                {
#if INTEROP
                    // Tier A: Il2Cpp 反射（直接在 il2cpp 类型系统上找字段/属性）
                    try
                    {
                        Il2CppSystem.Type it = iobj.GetIl2CppType();
                        if (it != null)
                        {
                            var flags = Il2CppSystem.Reflection.BindingFlags.Instance
                                | Il2CppSystem.Reflection.BindingFlags.Public
                                | Il2CppSystem.Reflection.BindingFlags.NonPublic;
                            try
                            {
                                var f = it.GetField(name, flags);
                                if (f != null)
                                {
                                    var v = f.GetValue(iobj);
                                    if (v != null)
                                        return v;
                                }
                            }
                            catch { }
                            // 注意：Il2CppSystem.Reflection.PropertyInfo 只有 GetValue(object) 单参重载，
                            // 没有 GetValue(object, object[]) 双参重载。若在此强类型调用双参版本，
                            // 整个方法在首次 JIT 时会抛 "Method not found"，且无法被本方法内 try-catch 捕获，
                            // 直接冒泡导致调用方（moc3 读取等）整体失败。故 Tier A 只读字段，属性交给 Tier B/C。
                        }
                    }
                    catch { }
#endif
                    // Tier B: 找到 il2cpp 类型对应的托管代理类，用 IntPtr 重新包装后 System 反射
                    // （interop/unhollower 代理程序集把 il2cpp 字段暴露为同名属性）
                    try
                    {
                        string typeName = GetIl2CppTypeName(iobj);
                        if (!string.IsNullOrEmpty(typeName))
                        {
                            object v = GetViaProxy(iobj, typeName, name);
                            if (v != null)
                                return v;
                        }
                    }
                    catch { }
                }
            }
            catch { }
#endif
            // Tier C: System 反射（Mono，或代理已是正确类型）
            return GetFieldOrPropManaged(obj.GetType(), obj, name);
        }

#if CPP
        private static string GetIl2CppTypeName(Il2CppSystem.Object iobj)
        {
            try
            {
                var t = iobj.GetIl2CppType();
                if (t != null)
                    return t.FullName ?? t.Name;
            }
            catch { }
            return null;
        }

        // il2cpp 类型名 → 托管代理 Type 缓存
        private static readonly Dictionary<string, System.Type> proxyTypeCache = new();

        internal static System.Type FindProxyType(string fullName)
        {
            lock (proxyTypeCache)
            {
                if (proxyTypeCache.TryGetValue(fullName, out System.Type cached))
                    return cached;
            }

            System.Type found = null;
            try
            {
                foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        System.Type t = asm.GetType(fullName, false);
                        if (t != null)
                        {
                            found = t;
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            lock (proxyTypeCache)
            {
                proxyTypeCache[fullName] = found;
            }
            return found;
        }

        /// <summary>
        /// 用 IntPtr 构造正确类型的代理实例，然后 System 反射读取属性/字段。
        /// 代理程序集（interop/unhollower）把每个 il2cpp 字段都暴露为同名托管属性。
        /// </summary>
        private static object GetViaProxy(Il2CppSystem.Object iobj, string typeName, string name)
        {
            try
            {
                System.Type proxyType = FindProxyType(typeName);
                if (proxyType == null)
                    return null;

                object inst = null;
                try
                {
                    // 代理类均有 public ctor(IntPtr)
                    inst = System.Activator.CreateInstance(proxyType, new object[] { iobj.Pointer });
                }
                catch { return null; }
                if (inst == null)
                    return null;

                try
                {
                    PropertyInfo p = proxyType.GetProperty(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null && p.GetIndexParameters().Length == 0)
                    {
                        object v = p.GetValue(inst, null);
                        if (v != null)
                            return v;
                    }
                }
                catch { }

                try
                {
                    FieldInfo f = proxyType.GetField(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null)
                    {
                        object v = f.GetValue(inst);
                        if (v != null)
                            return v;
                    }
                }
                catch { }
            }
            catch { }
            return null;
        }
#endif

        /// <summary>
        /// System 反射读取字段或属性（含基类查找）
        /// </summary>
        private static object GetFieldOrPropManaged(System.Type t, object obj, string name)
        {
            try
            {
                System.Type cur = t;
                int guard = 0;
                while (cur != null && cur != typeof(object) && guard++ < 6)
                {
                    try
                    {
                        FieldInfo f = cur.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (f != null)
                            return f.GetValue(obj);
                    }
                    catch { }
                    try
                    {
                        PropertyInfo p = cur.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (p != null && p.GetIndexParameters().Length == 0)
                            return p.GetValue(obj, null);
                    }
                    catch { }
                    cur = cur.BaseType;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 读取对象的所有实例字段名和值（用于诊断与 TextAsset 字段扫描）。
        /// </summary>
        private static List<KeyValuePair<string, object>> GetAllFieldValues(object obj)
        {
            List<KeyValuePair<string, object>> result = new();
            if (obj == null)
                return result;

#if CPP
            try
            {
                Il2CppSystem.Object iobj = obj.TryCast<Il2CppSystem.Object>();
                if (iobj != null)
                {
                    Il2CppSystem.Type it = null;
                    try { it = iobj.GetIl2CppType(); } catch { }
                    if (it != null)
                    {
                        var flags = Il2CppSystem.Reflection.BindingFlags.Instance
                            | Il2CppSystem.Reflection.BindingFlags.Public
                            | Il2CppSystem.Reflection.BindingFlags.NonPublic;
#if INTEROP
                        try
                        {
                            var fields = it.GetFields(flags);
                            if (fields != null)
                            {
                                foreach (var f in fields)
                                {
                                    try
                                    {
                                        string fname = f.Name;
                                        if (string.IsNullOrEmpty(fname)) continue;
                                        object v = GetFieldOrPropSingle(obj, fname);
                                        result.Add(new KeyValuePair<string, object>(fname, v));
                                    }
                                    catch { }
                                }
                                if (result.Count > 0)
                                    return result;
                            }
                        }
                        catch { }
#endif
                    }
                }
            }
            catch { }
#endif
            // Mono / 兜底：System 反射
            try
            {
                System.Type cur = obj.GetType();
                int guard = 0;
                while (cur != null && cur != typeof(object) && guard++ < 4)
                {
                    FieldInfo[] fields = cur.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (FieldInfo f in fields)
                    {
                        try
                        {
                            object v = f.GetValue(obj);
                            result.Add(new KeyValuePair<string, object>(f.Name, v));
                        }
                        catch { }
                    }
                    cur = cur.BaseType;
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 打印组件/对象的字段名和值摘要（每种类型只打印一次，用于诊断反射链问题）。
        /// </summary>
        private static void DumpComponentValuesOnce(object obj, string typeLabel)
        {
            try
            {
                if (obj == null) return;
                if (!dumpedComponentTypes.Add(typeLabel)) return;

                List<KeyValuePair<string, object>> vals = GetAllFieldValues(obj);
                if (vals.Count == 0)
                {
                    ExplorerCore.Log($"[诊断] {typeLabel}: 无可读字段");
                    return;
                }

                StringBuilder sb = new StringBuilder();
                int n = 0;
                foreach (var kv in vals)
                {
                    if (n++ > 40) { sb.Append("..."); break; }
                    string vdesc;
                    if (kv.Value == null) vdesc = "null";
                    else
                    {
                        try { vdesc = kv.Value.GetType().Name; } catch { vdesc = "?"; }
                    }
                    if (sb.Length < 1200)
                        sb.Append(kv.Key).Append('=').Append(vdesc).Append(", ");
                }
                ExplorerCore.Log($"[诊断] {typeLabel} 字段: {sb}");
            }
            catch { }
        }

        /// <summary>
        /// 通用枚举：兼容 System.Collections.IEnumerable 与 Il2CppSystem.Array
        /// </summary>
        private static List<object> EnumerateObjects(object val)
        {
            List<object> result = new();
            if (val == null)
                return result;
#if CPP
            try
            {
                Il2CppSystem.Array arr = val.TryCast<Il2CppSystem.Array>();
                if (arr != null)
                {
                    int len = arr.Length;
                    for (int i = 0; i < len; i++)
                    {
                        object o = arr.GetValue(i);
                        if (o != null)
                            result.Add(o);
                    }
                    return result;
                }
            }
            catch { }
#endif
            try
            {
                if (val is System.Collections.IEnumerable en)
                {
                    foreach (object o in en)
                        result.Add(o);
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 将对象安全转换为 byte[]（兼容 System.Byte[] 与 Il2Cpp 数组代理）。
        /// 快速路径：调用数组代理的隐式转换运算符（原生 memcpy）。
        /// </summary>
        private static byte[] TryConvertToByteArray(object val)
        {
            try
            {
                if (val == null) return null;
                if (val is byte[] sysArr) return sysArr;
#if CPP
                // 快速路径1：op_Implicit 隐式转换（Il2CppStructArray<T>/Il2CppArrayBase<T> 都有）
                try
                {
                    System.Type vt = val.GetType();
                    foreach (MethodInfo m in vt.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "op_Implicit") continue;
                        if (m.ReturnType != typeof(byte[])) continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(vt))
                        {
                            byte[] fast = (byte[])m.Invoke(null, new object[] { val });
                            if (fast != null && fast.Length > 0)
                                return fast;
                        }
                    }
                }
                catch { }

                // 快速路径2：Il2CppStructArray<byte> 直接索引访问（byte 是值类型，索引器直读底层内存）
                // 仅 Interop 有该类型；Unhollower 直接走慢速路径（Il2CppSystem.Array 逐元素）
#if INTEROP
                try
                {
                    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte> sarr =
                        val.TryCast<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>>();
                    if (sarr != null && sarr.Length > 0)
                    {
                        byte[] ret = new byte[sarr.Length];
                        for (int i = 0; i < sarr.Length; i++)
                            ret[i] = sarr[i];
                        return ret;
                    }
                }
                catch { }
#endif

                // 慢速路径：Il2CppSystem.Array 逐元素（不限大小，moc3/TextAsset 常超 4KB）
                try
                {
                    Il2CppSystem.Array arr = val.TryCast<Il2CppSystem.Array>();
                    if (arr != null && arr.Length > 0)
                    {
                        int n = arr.Length;
                        byte[] ret = new byte[n];
                        for (int i = 0; i < n; i++)
                        {
                            object o = arr.GetValue(i);
                            if (o is byte b) ret[i] = b;
                            else ret[i] = System.Convert.ToByte(o);
                        }
                        return ret;
                    }
                }
                catch { }

                // 诊断（仅首次失败时输出，避免刷屏）
                if (!byteArrayDiagLogged)
                {
                    byteArrayDiagLogged = true;
                    try
                    {
                        string tn = val.GetType().FullName;
                        int alen = -1;
                        try { Il2CppSystem.Array a = val.TryCast<Il2CppSystem.Array>(); alen = a != null ? a.Length : -1; } catch { }
                        ExplorerCore.LogWarning($"[诊断] 字节数组转换失败: 类型={tn}, Il2CppArray.Length={alen}");
                    }
                    catch { }
                }
#endif
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 添加贴图导出项（按实例ID去重）
        /// </summary>
        private static bool AddTextureItem(Texture2D tex, List<ExportableItem> items, string typeLabel)
        {
            try
            {
                if (tex == null) return false;
                int id = tex.GetInstanceID();
                if (collectedTexIds.Contains(id)) return false;
                collectedTexIds.Add(id);

                string name = string.IsNullOrEmpty(tex.name) ? "texture_" + id : tex.name;
                string key = name + "_" + typeLabel;
                if (exportedNames.Contains(key)) return false;
                exportedNames.Add(key);

                Texture2D captured = tex;
                items.Add(new ExportableItem
                {
                    Name = name,
                    Type = typeLabel,
                    Asset = captured,
                    ExportAction = (path) => ExportTextureAsPNG(captured, path)
                });
                return true;
            }
            catch { return false; }
        }

        #endregion

        #region 扩展名检测

        /// <summary>
        /// 检测 TextAsset 的扩展名
        /// </summary>
        private static string DetectTextAssetExtension(TextAsset ta)
        {
            if (ta == null)
                return "txt";

            // 方法1：读字节检测文件头
            try
            {
                byte[] bytes = ReadTextAssetFull(ta);
                if (bytes != null && bytes.Length >= 4)
                {
                    if (bytes[0] == 0x4D && bytes[1] == 0x4F && bytes[2] == 0x43 && bytes[3] == 0x33)
                        return "moc3";
                    if (bytes[0] == 0x6D && bytes[1] == 0x6F && bytes[2] == 0x63)
                        return "moc";
                    if (bytes[0] == 0x7B && bytes[1] == 0x22)
                        return "json";
                    if (bytes[0] == 0x50 && bytes[1] == 0x4B)
                        return "zip";
                }
            }
            catch { }

            // 方法2：按名称判断（bytes 可能不可读）
            string name = ta.name?.ToLower() ?? "";
            if (name.Contains(".moc3") || name.Contains("moc3"))
                return "moc3";
            if (name.Contains(".moc") || name.Contains("moc_") || name.Contains("_moc"))
                return "moc";
            if (name.EndsWith(".json") || name.Contains(".json"))
                return "json";
            if (name.Contains("atlas"))
                return "atlas.txt";
            if (name.EndsWith(".csv") || name.Contains(".csv"))
                return "csv";
            if (name.EndsWith(".txt"))
                return "txt";
            if (name.EndsWith(".xml") || name.Contains(".xml"))
                return "xml";
            if (name.Contains(".skel"))
                return "skel.bytes";

            return "txt";
        }

        #endregion

        #region 导出实现

        /// <summary>
        /// 导出 TextAsset
        /// </summary>
        public static void ExportTextAsset(TextAsset ta, string path, string extension)
        {
            if (ta == null)
            {
                ExplorerCore.LogWarning("TextAsset 为空");
                return;
            }

            if (!path.EndsWith("." + extension, System.StringComparison.OrdinalIgnoreCase))
                path += "." + extension;

            path = EnsureValidPath(path);

            try
            {
                byte[] bytes = ReadTextAssetFull(ta);
                if (bytes != null && bytes.Length > 0)
                {
                    File.WriteAllBytes(path, bytes);
                }
                else
                {
                    File.WriteAllText(path, ta.text);
                }
                ExplorerCore.Log($"数据文件已导出: {path}");
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"导出数据文件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 导出字节数组
        /// </summary>
        public static void ExportBytes(byte[] bytes, string path, string extension)
        {
            if (bytes == null || bytes.Length == 0)
            {
                ExplorerCore.LogWarning("字节数据为空");
                return;
            }

            if (!path.EndsWith("." + extension, System.StringComparison.OrdinalIgnoreCase))
                path += "." + extension;

            path = EnsureValidPath(path);

            try
            {
                File.WriteAllBytes(path, bytes);
                ExplorerCore.Log($"字节数据已导出: {path}");
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"导出字节数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 导出文本内容
        /// </summary>
        public static void ExportText(string content, string path, string extension)
        {
            if (string.IsNullOrEmpty(content))
            {
                ExplorerCore.LogWarning("文本内容为空");
                return;
            }

            if (!path.EndsWith("." + extension, System.StringComparison.OrdinalIgnoreCase))
                path += "." + extension;

            path = EnsureValidPath(path);

            try
            {
                File.WriteAllText(path, content, new UTF8Encoding(false));
                ExplorerCore.Log($"文本已导出: {path}");
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"导出文本失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 导出纹理为 PNG（安全版本，处理不可读、压缩纹理）。
        /// 不再依赖 UniverseLib TextureHelper（其内部构造函数在 IL2CPP/Unity 2022 下常被剥离）。
        /// 优先 ImageConversion.EncodeToPNG，失败时用 RenderTexture + ReadPixels 兜底。
        /// </summary>
        public static void ExportTextureAsPNG(Texture2D texture, string path)
        {
            if (texture == null)
            {
                ExplorerCore.LogWarning("纹理为空，无法导出！");
                return;
            }

            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                path += ".png";

            path = EnsureValidPath(path);

            // 方法1：ImageConversion.EncodeToPNG（Unity 2017+ 标准静态 API）
            try
            {
                byte[] bytes = EncodeToPNG(texture);
                if (bytes != null && bytes.Length > 0)
                {
                    File.WriteAllBytes(path, bytes);
                    ExplorerCore.Log($"纹理已导出: {path}");
                    return;
                }
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"EncodeToPNG 导出失败: {ex.Message}");
            }

            // 方法1.5（v16ao）：可读贴图直接 GetPixels32 + 托管 PNG 编码。
            // 完全不创建任何 Texture2D —— 免疫「Texture2D 参数化构造函数被剥离」的 IL2CPP 游戏
            // （此类游戏 interop 程序集里 Texture2D 只有 .ctor(IntPtr)，任何 new 都必失败）。
            try
            {
                if (IsTextureReadableReflection(texture))
                {
                    int tw = texture.width, th = texture.height;
                    Color32[] px = null;
                    try { px = texture.GetPixels32(); } catch { }
                    if (px != null && px.Length >= tw * th && tw > 0 && th > 0)
                    {
                        byte[] rgba0 = Color32ToRgba(px);
                        FlipVertically(rgba0, tw, th);   // Unity 行 0 在底部 → PNG 行 0 在顶部
                        byte[] bytes0 = EncodePngManaged(tw, th, rgba0);
                        if (bytes0 != null && bytes0.Length > 0)
                        {
                            File.WriteAllBytes(path, bytes0);
                            ExplorerCore.Log($"纹理已导出(直读像素): {path}");
                            return;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"直读像素导出失败: {ex.Message}");
            }

            // 方法2：RenderTexture + ReadPixels 兜底（处理不可读/压缩纹理）
            try
            {
                ExportTextureViaReadPixels(texture, path);
                ExplorerCore.Log($"纹理已导出(ReadPixels): {path}");
                return;
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"ReadPixels 导出失败: {ex.Message}");
            }

            ExplorerCore.LogWarning($"纹理导出失败（无法读取像素）: {texture.name}");
        }

        /// <summary>Color32[] → RGBA8888 字节数组（长度固定 4×N）。</summary>
        private static byte[] Color32ToRgba(Color32[] px)
        {
            byte[] rgba = new byte[px.Length * 4];
            for (int i = 0; i < px.Length; i++)
            {
                rgba[i * 4] = px[i].r;
                rgba[i * 4 + 1] = px[i].g;
                rgba[i * 4 + 2] = px[i].b;
                rgba[i * 4 + 3] = px[i].a;
            }
            return rgba;
        }

        /// <summary>
        /// 反射调用 ImageConversion.EncodeToPNG 或 Texture2D.EncodeToPNG。
        /// </summary>
        private static byte[] EncodeToPNG(Texture2D texture)
        {
            // 优先 UnityEngine.ImageConversion.EncodeToPNG（静态，Unity 2017.3+）
            try
            {
                System.Type imageConversionType = System.Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule", false, false)
                    ?? System.Type.GetType("UnityEngine.ImageConversion, UnityEngine.CoreModule", false, false);
                if (imageConversionType != null)
                {
                    System.Reflection.MethodInfo m = imageConversionType.GetMethod("EncodeToPNG",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (m != null)
                    {
                        byte[] bytes = m.Invoke(null, new object[] { texture }) as byte[];
                        if (bytes != null && bytes.Length > 0)
                            return bytes;
                    }
                }
            }
            catch { }

            // 兜底：Texture2D 实例方法 EncodeToPNG
            try
            {
                System.Reflection.MethodInfo m = typeof(Texture2D).GetMethod("EncodeToPNG",
                    BindingFlags.Public | BindingFlags.Instance);
                if (m != null)
                    return m.Invoke(texture, null) as byte[];
            }
            catch { }

            return null;
        }

        /// <summary>
        /// RenderTexture + ReadPixels 方式导出纹理。
        /// 兼容不可读（isReadable=false）和压缩纹理。
        /// </summary>
        /// <summary>
        /// 反射读取 Texture2D.isReadable（net35 编译门面缺该属性，Mono 运行时反射）。
        /// v16an：不再限制 Mono——IL2CPP 下直接访问 .isReadable 属性同样存在
        /// 被剥离后 JIT MissingMethodException 的风险，统一走反射。
        /// </summary>
        private static bool IsTextureReadableReflection(Texture2D tex)
        {
            try
            {
                System.Reflection.PropertyInfo p = tex.GetType().GetProperty("isReadable");
                if (p != null)
                {
                    object v = p.GetValue(tex, null);
                    return v is bool b && b;
                }
                return true; // 无该属性时假定可读，失败由调用方异常兜底
            }
            catch { return false; }
        }

        /// <summary>
        /// v16an：反射创建 RGBA32 临时贴图（IL2CPP 安全版）。见 CreateTempTexture 的分层策略。
        /// </summary>
        private static Texture2D CreateTempTexture(int w, int h)
        {
            string why1 = null, why2 = null, why3 = null;

            // ① 反射参数化构造：Mono / Unhollower / 未被剥离的 IL2CPP 游戏走这条
            Texture2D tex = TryCreateTextureViaCtor(w, h, out why1);
            if (tex != null)
            {
                ExplorerCore.Log($"[贴图] 临时贴图创建成功(①反射构造) {w}x{h}");
                return tex;
            }

            // ② 借用引擎内置白贴图（原生对象一定存在）+ Resize 重新分配尺寸
            tex = TryCreateTextureViaBuiltin(w, h, false, out why2);
            if (tex != null)
            {
                ExplorerCore.Log($"[贴图] 临时贴图创建成功(②内置贴图+Resize) {w}x{h}");
                return tex;
            }

            // ③ 借用引擎内置白贴图 + internal static Internal_Create（Unity 构造函数内部的真实实现）
            tex = TryCreateTextureViaBuiltin(w, h, true, out why3);
            if (tex != null)
            {
                ExplorerCore.Log($"[贴图] 临时贴图创建成功(③内置贴图+Internal_Create) {w}x{h}");
                return tex;
            }

            throw new System.Exception(
                "创建 Texture2D 失败：①反射构造[" + why1 + "] ②内置贴图+Resize[" + why2 + "] ③内置贴图+Internal_Create[" + why3 + "]");
        }

        /// <summary>异常消息解包（反射调用会包一层 TargetInvocationException，内层才是真因）。</summary>
        private static string ExMsg(System.Exception e)
        {
            System.Exception cur = e;
            while (cur.InnerException != null)
                cur = cur.InnerException;

            // v16at：IL2CPP 里 native（C++）抛出的异常不以 System.Exception 派生，
            //   .NET 会包成 RuntimeWrappedException，其 Message 只有一句没用的话。
            //   真正的类型在 WrappedException 里 —— 不解出来就会像上面那次自检一样
            //   只看到「An object that does not derive from System.Exception...」，
            //   完全不知道是哪个原生调用炸的。
            try
            {
                if (cur.GetType().FullName == "System.Runtime.CompilerServices.RuntimeWrappedException")
                {
                    System.Reflection.PropertyInfo p = cur.GetType().GetProperty("WrappedException");
                    object wrapped = p != null ? p.GetValue(cur, null) : null;
                    if (wrapped != null)
                        return "原生异常[" + wrapped.GetType().FullName + "] " + wrapped.ToString();
                }
            }
            catch { }

            return cur.GetType().Name + ": " + cur.Message;
        }

        /// <summary>① 反射调用 Texture2D 的参数化构造函数（4参 → 5参 → 2参）。</summary>
        private static Texture2D TryCreateTextureViaCtor(int w, int h, out string why)
        {
            why = null;
            try
            {
                System.Type t = typeof(Texture2D);
                System.Reflection.ConstructorInfo c4 = t.GetConstructor(
                    new[] { typeof(int), typeof(int), typeof(TextureFormat), typeof(bool) });
                if (c4 != null)
                {
                    try { return c4.Invoke(new object[] { w, h, TextureFormat.RGBA32, false }) as Texture2D; }
                    catch (System.Exception e) { why = "4参 " + ExMsg(e); }
                }
                else why = "无4参";

                System.Reflection.ConstructorInfo c5 = t.GetConstructor(
                    new[] { typeof(int), typeof(int), typeof(TextureFormat), typeof(bool), typeof(bool) });
                if (c5 != null)
                {
                    try { return c5.Invoke(new object[] { w, h, TextureFormat.RGBA32, false, false }) as Texture2D; }
                    catch (System.Exception e) { why += " / 5参 " + ExMsg(e); }
                }
                else why += " / 无5参";

                System.Reflection.ConstructorInfo c2 = t.GetConstructor(new[] { typeof(int), typeof(int) });
                if (c2 != null)
                {
                    try { return c2.Invoke(new object[] { w, h }) as Texture2D; }
                    catch (System.Exception e) { why += " / 2参 " + ExMsg(e); }
                }
                else why += " / 无2参";
            }
            catch (System.Exception e) { why = ExMsg(e); }
            return null;
        }

        /// <summary>
        /// 取一张「原生对象一定存在」的 Texture2D 作为底板。
        /// 首选 Instantiate(Texture2D.whiteTexture) 拿独立克隆；失败则直接用引擎共享白贴图（置 _borrowedShared=true，用完必须还原）。
        /// </summary>
        private static Texture2D GetScratchBasisTexture(out bool borrowedShared)
        {
            borrowedShared = false;
            Texture2D white = null;
            try
            {
                System.Reflection.PropertyInfo p = typeof(Texture2D).GetProperty(
                    "whiteTexture", BindingFlags.Public | BindingFlags.Static);
                if (p != null) white = p.GetValue(null, null) as Texture2D;
            }
            catch { }
            if (white == null) return null;

            // 克隆一份，避免污染引擎全局白贴图
            try
            {
                foreach (System.Reflection.MethodInfo m in typeof(UnityEngine.Object).GetMethods(
                             BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "Instantiate" || !m.IsGenericMethodDefinition) continue;
                    if (m.GetParameters().Length != 1) continue;
                    System.Reflection.MethodInfo g = m.MakeGenericMethod(typeof(Texture2D));
                    try
                    {
                        Texture2D clone = g.Invoke(null, new object[] { white }) as Texture2D;
                        if (clone != null) return clone;
                    }
                    catch { }
                }
            }
            catch { }

            borrowedShared = true;
            _borrowedWhiteTexture = white;
            _borrowedWhiteRestored = false;
            return white;
        }

        // 被借用的引擎共享白贴图（借用后必须还原，且绝不能 Destroy 引擎内置对象）
        private static Texture2D _borrowedWhiteTexture;
        private static bool _borrowedWhiteRestored;

        /// <summary>释放临时贴图：借用共享白贴图的场合改为还原（绝不销毁引擎资产）。</summary>
        private static void ReleaseTempTexture(Texture2D temp)
        {
            if (temp == null) return;
            if (_borrowedWhiteTexture != null && ReferenceEquals(temp, _borrowedWhiteTexture))
            {
                if (!_borrowedWhiteRestored)
                {
                    TryRestoreSharedWhiteTexture();
                    _borrowedWhiteRestored = true;
                }
                return;
            }
            try { UnityEngine.Object.Destroy(temp); } catch { }
        }

        /// <summary>
        /// v16aq：创建临时 RenderTexture（多策略，绕开被剥离的 RenderTextureDescriptor 构造）。
        /// ① `new RenderTexture(w,h,0,RenderTextureFormat.ARGB32)` → ② `new RenderTexture(w,h,0)` → ③ `RenderTexture.GetTemporary(...)`。
        /// how 回传实际使用的方式（"ctor" / "ctor3" / "temporary"），决定释放走 Destroy 还是 ReleaseTemporary。
        /// </summary>
        private static RenderTexture CreateTempRenderTexture(int w, int h, out string how)
        {
            how = null;
            System.Type t = typeof(RenderTexture);

            // ① 带 format 枚举的构造
            System.Reflection.ConstructorInfo c4 = t.GetConstructor(
                new[] { typeof(int), typeof(int), typeof(int), typeof(RenderTextureFormat) });
            if (c4 != null)
            {
                try
                {
                    RenderTexture rt = c4.Invoke(new object[] { w, h, 0, RenderTextureFormat.ARGB32 }) as RenderTexture;
                    if (rt != null) { how = "ctor"; TryCreateRT(rt); return rt; }
                }
                catch { }
            }

            // ② 仅 (w,h,depth)
            System.Reflection.ConstructorInfo c3 = t.GetConstructor(
                new[] { typeof(int), typeof(int), typeof(int) });
            if (c3 != null)
            {
                try
                {
                    RenderTexture rt = c3.Invoke(new object[] { w, h, 0 }) as RenderTexture;
                    if (rt != null) { how = "ctor3"; TryCreateRT(rt); return rt; }
                }
                catch { }
            }

            // ③ 退化：GetTemporary（在未被剥离的游戏里可用）
            System.Reflection.MethodInfo gt = null;
            foreach (System.Reflection.MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "GetTemporary") continue;
                System.Reflection.ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 4 && ps[0].ParameterType == typeof(int)) { gt = m; break; }
            }
            if (gt != null)
            {
                try
                {
                    RenderTexture rt = gt.Invoke(null, new object[] { w, h, 0, RenderTextureFormat.ARGB32 }) as RenderTexture;
                    if (rt != null) { how = "temporary"; return rt; }
                }
                catch { }
            }

            throw new System.Exception("无法创建 RenderTexture（构造与 GetTemporary 均不可用）");
        }

        /// <summary>RT 构造后调用 Create() 显式分配（失败可忽略，首次使用时也会惰性创建）。</summary>
        private static void TryCreateRT(RenderTexture rt)
        {
            try
            {
                System.Reflection.MethodInfo c = typeof(RenderTexture).GetMethod(
                    "Create", BindingFlags.Public | BindingFlags.Instance, null, System.Type.EmptyTypes, null);
                if (c != null) c.Invoke(rt, null);
            }
            catch { }
        }

        /// <summary>释放临时 RenderTexture：GetTemporary 来的走 ReleaseTemporary，构造来的走 Destroy。</summary>
        private static void ReleaseTempRenderTexture(RenderTexture rt, string how)
        {
            if (rt == null) return;
            try
            {
                if (how == "temporary")
                {
                    RenderTexture.ReleaseTemporary(rt);
                }
                else
                {
                    UnityEngine.Object.Destroy(rt);
                }
            }
            catch { }
        }

        /// <summary>
        /// ②/③ 用底板贴图重新分配成 w×h RGBA32：
        /// useInternalCreate=false → 调实例方法 Resize(w,h,fmt,hasMipMap)；
        /// useInternalCreate=true  → 调 internal static Internal_Create(mono,w,h,mipCount,fmt,linear[,nativeTex])（Unity 构造函数的真实内核）。
        /// </summary>
        private static Texture2D TryCreateTextureViaBuiltin(int w, int h, bool useInternalCreate, out string why)
        {
            why = null;
            Texture2D basis = null;
            bool shared = false;
            try
            {
                basis = GetScratchBasisTexture(out shared);
                if (basis == null) { why = "引擎白贴图不可用"; return null; }

                if (!useInternalCreate)
                {
                    System.Reflection.MethodInfo resize = typeof(Texture2D).GetMethod("Resize",
                        new[] { typeof(int), typeof(int), typeof(TextureFormat), typeof(bool) });
                    if (resize == null) { why = "无 Resize(int,int,TextureFormat,bool)"; return null; }
                    resize.Invoke(basis, new object[] { w, h, TextureFormat.RGBA32, false });
                }
                else
                {
                    System.Reflection.MethodInfo create = null;
                    foreach (System.Reflection.MethodInfo m in typeof(Texture2D).GetMethods(
                                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (m.Name == "Internal_Create") { create = m; break; }
                        if (m.Name == "Internal_CreateImpl" && create == null) create = m;
                    }
                    if (create == null) { why = "无 Internal_Create"; return null; }

                    // 参数个数随 Unity 版本略有差别：前 6 个固定，其余补 IntPtr.Zero
                    System.Reflection.ParameterInfo[] ps = create.GetParameters();
                    if (ps.Length < 6) { why = "Internal_Create 参数过少(" + ps.Length + ")"; return null; }
                    object[] args = new object[ps.Length];
                    args[0] = basis;
                    args[1] = w;
                    args[2] = h;
                    args[3] = 1;                     // mipCount（mipChain=false）
                    args[4] = TextureFormat.RGBA32;  // 枚举/整数均可被反射转换
                    args[5] = false;                 // linear
                    for (int i = 6; i < ps.Length; i++) args[i] = System.IntPtr.Zero;
                    create.Invoke(null, args);
                }

                if (basis.width != w || basis.height != h)
                {
                    why = $"尺寸校验失败 {basis.width}x{basis.height}";
                    return null;
                }
                return basis;
            }
            catch (System.Exception e)
            {
                why = ExMsg(e);
                // 借用失败时不要留下被改坏的共享白贴图
                if (shared && basis != null) TryRestoreSharedWhiteTexture();
                return null;
            }
        }

        /// <summary>把被借用的引擎共享白贴图还原成 4x4 纯白（避免影响引擎/其它插件渲染）。</summary>
        private static void TryRestoreSharedWhiteTexture()        {
            try
            {
                System.Reflection.PropertyInfo p = typeof(Texture2D).GetProperty(
                    "whiteTexture", BindingFlags.Public | BindingFlags.Static);
                Texture2D white = p != null ? p.GetValue(null, null) as Texture2D : null;
                if (white == null) return;

                System.Reflection.MethodInfo resize = typeof(Texture2D).GetMethod("Resize",
                    new[] { typeof(int), typeof(int), typeof(TextureFormat), typeof(bool) });
                if (resize == null) return;
                resize.Invoke(white, new object[] { 4, 4, TextureFormat.RGBA32, false });

                Color32[] px = new Color32[16];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
                white.SetPixels32(px);
                white.Apply();
            }
            catch { }
        }

        private static void ExportTextureViaReadPixels(Texture src, string path)
        {
            if (src == null)
                throw new System.Exception("源纹理为空");

            int w = src.width;
            int h = src.height;
            if (w <= 0 || h <= 0)
                throw new System.Exception("纹理尺寸非法");

            // 使用反射构造函数选择（v16an：4参构造在某些 IL2CPP 游戏被剥离）
            Texture2D temp = CreateTempTexture(w, h);
            try
            {
                // 源就是可读写 Texture2D：直接复制像素
                // （v16an：isReadable 统一反射读取，IL2CPP 下避免 JIT MissingMethodException）
                Texture2D srcTex2D = src as Texture2D;
                bool srcReadable = false;
                if (srcTex2D != null)
                {
                    srcReadable = IsTextureReadableReflection(srcTex2D);
                }
                if (srcReadable)
                {
                    temp.SetPixels(srcTex2D.GetPixels());
                    temp.Apply();
                }
                else
                {
                    // 不可读/压缩纹理：Blit 到 RenderTexture 再 ReadPixels
                    // v16aq：RT 创建改走多策略，绕开 GetTemporary 内部被剥离的 RenderTextureDescriptor 构造
                    string rtHow;
                    RenderTexture rt = CreateTempRenderTexture(w, h, out rtHow);
                    RenderTexture prevActive = RenderTexture.active;
                    try
                    {
                        Graphics.Blit(src, rt);
                        RenderTexture.active = rt;
                        temp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                        temp.Apply();
                    }
                    finally
                    {
                        RenderTexture.active = prevActive;
                        ReleaseTempRenderTexture(rt, rtHow);
                    }
                }

                byte[] bytes = null;
                try { bytes = EncodeToPNG(temp); } catch { }
                if (bytes == null || bytes.Length == 0)
                {
                    // EncodeToPNG 被剥离：托管 PNG 编码器兜底（自实现 zlib/deflate，免疫剥离）
                    byte[] rgba;
                    Color32[] px = null;
                    try { px = temp.GetPixels32(); } catch { }
                    if (px != null)
                    {
                        rgba = new byte[px.Length * 4];
                        for (int i = 0; i < px.Length; i++)
                        {
                            rgba[i * 4] = px[i].r;
                            rgba[i * 4 + 1] = px[i].g;
                            rgba[i * 4 + 2] = px[i].b;
                            rgba[i * 4 + 3] = px[i].a;
                        }
                    }
                    else
                    {
                        Color[] pc = temp.GetPixels();
                        rgba = new byte[pc.Length * 4];
                        for (int i = 0; i < pc.Length; i++)
                        {
                            rgba[i * 4] = (byte)(pc[i].r * 255f);
                            rgba[i * 4 + 1] = (byte)(pc[i].g * 255f);
                            rgba[i * 4 + 2] = (byte)(pc[i].b * 255f);
                            rgba[i * 4 + 3] = (byte)(pc[i].a * 255f);
                        }
                    }
                    // GetPixels32/GetPixels 返回的是 Unity 纹理坐标（行 0 为底部），
                    // PNG 扫描行 0 应为顶部，所以先垂直翻转
                    FlipVertically(rgba, w, h);
                    bytes = EncodePngManaged(w, h, rgba);
                }
                if (bytes != null && bytes.Length > 0)
                    File.WriteAllBytes(path, bytes);
                else
                    throw new System.Exception("PNG 编码失败（EncodeToPNG 与托管编码器均失败）");
            }
            finally
            {
                ReleaseTempTexture(temp);
            }
        }

        #region 托管 PNG 编码器（不依赖 ImageConversion.EncodeToPNG，免疫 IL2CPP 剥离）

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>
        /// 纯托管 PNG 编码（RGBA8888，滤波方式 None，zlib = 0x78 0x9C + Deflate + Adler32）。
        /// </summary>
        private static byte[] EncodePngManaged(int width, int height, byte[] rgba)
        {
            if (rgba == null || rgba.Length < width * height * 4)
                return null;

            using (MemoryStream ms = new MemoryStream())
            {
                ms.Write(PngSignature, 0, 8);

                // IHDR：宽、高、位深8、颜色类型6(RGBA)、压缩0、滤波0、隔行0
                byte[] ihdr = new byte[13];
                WriteBE(ihdr, 0, (uint)width);
                WriteBE(ihdr, 4, (uint)height);
                ihdr[8] = 8;
                ihdr[9] = 6;
                WritePngChunk(ms, "IHDR", ihdr);

                // 原始像素：每行前加滤波字节 0（None）
                int stride = width * 4;
                byte[] raw = new byte[(stride + 1) * height];
                for (int y = 0; y < height; y++)
                {
                    raw[y * (stride + 1)] = 0;
                    Array.Copy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
                }

                // zlib 流：2字节头 + Deflate + Adler32（速度优先）
                byte[] idat;
                using (MemoryStream def = new MemoryStream())
                {
                    // 注意：CompressionLevel 重载是 .NET 4.5+ 才有，net35 只有 CompressionMode 2 参构造；
                    // 该构造会连带关闭底层流，但 MemoryStream 关闭后 ToArray() 依然可用。
                    using (var ds = new System.IO.Compression.DeflateStream(def, System.IO.Compression.CompressionMode.Compress))
                        ds.Write(raw, 0, raw.Length);
                    byte[] body = def.ToArray();
                    idat = new byte[2 + body.Length + 4];
                    idat[0] = 0x78;
                    idat[1] = 0x9C;
                    Array.Copy(body, 0, idat, 2, body.Length);
                    uint adler = Adler32(raw, 0, raw.Length);
                    WriteBE(idat, idat.Length - 4, adler);
                }
                WritePngChunk(ms, "IDAT", idat);
                WritePngChunk(ms, "IEND", new byte[0]);

                return ms.ToArray();
            }
        }

        /// <summary>将 RGBA 像素数组按图像中心水平翻转（行顺序反转）。</summary>
        private static void FlipVertically(byte[] rgba, int width, int height)
        {
            if (rgba == null || rgba.Length < width * height * 4 || height <= 1)
                return;
            int stride = width * 4;
            byte[] tmp = new byte[stride];
            for (int y = 0; y < height / 2; y++)
            {
                int top = y * stride;
                int bottom = (height - 1 - y) * stride;
                Array.Copy(rgba, top, tmp, 0, stride);
                Array.Copy(rgba, bottom, rgba, top, stride);
                Array.Copy(tmp, 0, rgba, bottom, stride);
            }
        }

        private static void WritePngChunk(Stream s, string type, byte[] data)
        {
            byte[] len = new byte[4];
            WriteBE(len, 0, (uint)(data != null ? data.Length : 0));
            s.Write(len, 0, 4);

            byte[] t = Encoding.ASCII.GetBytes(type);
            s.Write(t, 0, 4);

            if (data != null && data.Length > 0)
                s.Write(data, 0, data.Length);

            uint crc = Crc32(t, 0, 4);
            if (data != null && data.Length > 0)
                crc = Crc32Update(crc, data, 0, data.Length);
            byte[] crcB = new byte[4];
            WriteBE(crcB, 0, crc);
            s.Write(crcB, 0, 4);
        }

        private static void WriteBE(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static uint[] pngCrcTable;

        private static uint Crc32(byte[] data, int off, int len)
        {
            return Crc32Update(0, data, off, len);
        }

        private static uint Crc32Update(uint crc, byte[] data, int off, int len)
        {
            if (pngCrcTable == null)
            {
                pngCrcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    pngCrcTable[n] = c;
                }
            }
            uint c2 = crc ^ 0xFFFFFFFFu;
            for (int i = off; i < off + len && i < data.Length; i++)
                c2 = pngCrcTable[(c2 ^ data[i]) & 0xFF] ^ (c2 >> 8);
            return c2 ^ 0xFFFFFFFFu;
        }

        private static uint Adler32(byte[] data, int off, int len)
        {
            uint a = 1, b = 0;
            for (int i = off; i < off + len && i < data.Length; i++)
            {
                a = (a + data[i]) % 65521u;
                b = (b + a) % 65521u;
            }
            return (b << 16) | a;
        }

        #endregion

        /// <summary>
        /// 导出 Mesh 为 OBJ 格式
        /// </summary>
        public static void ExportMeshAsOBJ(Mesh mesh, string path)
        {
            if (!mesh)
            {
                ExplorerCore.LogWarning("网格为空，无法导出！");
                return;
            }

            if (!path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                path += ".obj";

            path = EnsureValidPath(path);

            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"# UnityExplorer OBJ Export: {mesh.name}");
                sb.AppendLine($"# Vertices: {mesh.vertexCount}");
                sb.AppendLine();

                // 顶点
                Vector3[] vertices = mesh.vertices;
                foreach (Vector3 v in vertices)
                    sb.AppendLine($"v {v.x:F6} {v.y:F6} {v.z:F6}");

                // UV
                Vector2[] uvs = mesh.uv;
                if (uvs != null && uvs.Length > 0)
                {
                    foreach (Vector2 uv in uvs)
                        sb.AppendLine($"vt {uv.x:F6} {uv.y:F6}");
                }

                // 法线
                Vector3[] normals = mesh.normals;
                if (normals != null && normals.Length > 0)
                {
                    foreach (Vector3 n in normals)
                        sb.AppendLine($"vn {n.x:F6} {n.y:F6} {n.z:F6}");
                }

                // 三角面
                int[] triangles = mesh.triangles;
                bool hasUV = uvs != null && uvs.Length > 0;
                bool hasNormal = normals != null && normals.Length > 0;

                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int i0 = triangles[i] + 1;
                    int i1 = triangles[i + 1] + 1;
                    int i2 = triangles[i + 2] + 1;

                    if (hasUV && hasNormal)
                        sb.AppendLine($"f {i0}/{i0}/{i0} {i1}/{i1}/{i1} {i2}/{i2}/{i2}");
                    else if (hasUV)
                        sb.AppendLine($"f {i0}/{i0} {i1}/{i1} {i2}/{i2}");
                    else if (hasNormal)
                        sb.AppendLine($"f {i0}//{i0} {i1}//{i1} {i2}//{i2}");
                    else
                        sb.AppendLine($"f {i0} {i1} {i2}");
                }

                File.WriteAllText(path, sb.ToString());
                ExplorerCore.Log($"网格已导出: {path}");
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"导出网格失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 导出 Mesh 为 BINARY FBX 7.5（静态几何：顶点/法线/UV/三角面）。
        /// 注意：BepInEx/MelonLoader 运行时无法使用 Autodesk FBX SDK，标准 FBX 无法包含
        ///   SkinnedMesh 骨骼动画数据；此实现导出静态网格几何（含蒙皮的网格仅导出几何、不含骨骼）。
        /// 坐标转换：Unity 左手系 → FBX 右手系（X 取反 + 反转三角面绕序）。
        /// 实现参考：fbx_test/gen_binary_fbx.py（Cube 测试已生成 3KB v5_binary.fbx 结构合规）
        /// </summary>
        public static void ExportMeshAsFBX(Mesh mesh, string path)
        {
            if (!mesh)
            {
                ExplorerCore.LogWarning("网格为空，无法导出！");
                return;
            }

            if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                path += ".fbx";

            path = EnsureValidPath(path);

            try
            {
                Vector3[] vertices = mesh.vertices;
                Vector3[] normals = mesh.normals;
                Vector2[] uvs = mesh.uv;
                int[] triangles = mesh.triangles;

                if (vertices == null || vertices.Length == 0 || triangles == null || triangles.Length < 3)
                {
                    ExplorerCore.LogWarning($"网格 '{mesh.name}' 无有效顶点/三角面，无法导出 FBX");
                    return;
                }

                bool hasN = normals != null && normals.Length == vertices.Length;
                bool hasU = uvs != null && uvs.Length == vertices.Length;

                // IDs：每 mesh 唯一（防 ID 冲突），同时保底 Material/Texture
                long geomId  = 900000000000L + System.Math.Abs((long)mesh.GetInstanceID());
                long modelId = geomId + 1;
                long matId   = geomId + 2;
                long texId   = geomId + 3;
                long docId   = 1000000000L;
                long rootId  = 1000000001L;

                var fbx = new FBXBinaryWriter();

                // ==================== 文件头 ====================
                fbx.WriteBytes(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \x00\x1a\x00"));  // 23 字节 magic
                fbx.WriteU32(7500);   // FBX version
                fbx.WriteU32(8);      // top-level node count
                fbx.WriteBytes(new byte[8]);   // 8 字节 padding（reference FBX 风格；SDK 解析时需要）

                // ==================== 1. FBXHeaderExtension ====================
                fbx.BeginNode("FBXHeaderExtension");
                fbx.WritePropLong(0);   // FBXHeaderVersion prop（占位）
                fbx.BeginNode("FBXHeaderVersion"); fbx.WritePropInt(1003); fbx.EndNode();
                fbx.BeginNode("FBXVersion"); fbx.WritePropInt(7500); fbx.EndNode();
                fbx.BeginNode("EncryptionType"); fbx.WritePropInt(0); fbx.EndNode();
                fbx.BeginNode("CreationTimeStamp");
                fbx.WritePropInt(2026); fbx.WritePropInt(9); fbx.WritePropInt(4);
                fbx.WritePropInt(12); fbx.WritePropInt(35); fbx.WritePropInt(0);
                fbx.EndNode();
                fbx.BeginNode("Creator"); fbx.WritePropString("UnityExplorer"); fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();

                // ==================== 2. GlobalSettings ====================
                fbx.BeginNode("GlobalSettings");
                fbx.WritePropLong(1000);   // Version
                fbx.BeginNode("Properties70");
                fbx.BeginNode("P");
                fbx.WritePropString("UpAxis"); fbx.WritePropString("int"); fbx.WritePropString("Integer"); fbx.WritePropString("");
                fbx.WritePropInt(1);
                fbx.EndNode();
                fbx.BeginNode("P");
                fbx.WritePropString("UpAxisSign"); fbx.WritePropString("int"); fbx.WritePropString("Integer"); fbx.WritePropString("");
                fbx.WritePropInt(1);
                fbx.EndNode();
                fbx.BeginNode("P");
                fbx.WritePropString("FrontAxis"); fbx.WritePropString("int"); fbx.WritePropString("Integer"); fbx.WritePropString("");
                fbx.WritePropInt(2);
                fbx.EndNode();
                fbx.BeginNode("P");
                fbx.WritePropString("FrontAxisSign"); fbx.WritePropString("int"); fbx.WritePropString("Integer"); fbx.WritePropString("");
                fbx.WritePropInt(1);
                fbx.EndNode();
                fbx.BeginNode("P");
                fbx.WritePropString("CoordAxis"); fbx.WritePropString("int"); fbx.WritePropString("Integer"); fbx.WritePropString("");
                fbx.WritePropInt(0);
                fbx.EndNode();
                fbx.BeginNode("P");
                fbx.WritePropString("CoordAxisSign"); fbx.WritePropString("int"); fbx.WritePropString("Integer"); fbx.WritePropString("");
                fbx.WritePropInt(1);
                fbx.EndNode();
                fbx.BeginNode("P");
                fbx.WritePropString("UnitScaleFactor"); fbx.WritePropString("double"); fbx.WritePropString("Number"); fbx.WritePropString("");
                fbx.WritePropDouble(1.0);
                fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();

                // ==================== 3. Documents ====================
                fbx.BeginNode("Documents");
                fbx.WritePropLong(docId);
                fbx.WritePropString("");
                fbx.WritePropString("Scene");
                fbx.BeginNode("SourceObject"); fbx.WritePropLong(0); fbx.EndNode();
                fbx.BeginNode("ActiveAnimStackName"); fbx.WritePropString(""); fbx.EndNode();
                fbx.BeginNode("RootNode"); fbx.WritePropLong(rootId); fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();

                // ==================== 4. References (空) ====================
                fbx.BeginNode("References");
                fbx.EndNode();

                // ==================== 5. Definitions ====================
                fbx.BeginNode("Definitions");
                fbx.BeginNode("Version"); fbx.WritePropInt(100); fbx.EndNode();
                fbx.BeginNode("Count"); fbx.WritePropInt(5); fbx.EndNode();
                fbx.BeginNode("ObjectType");
                fbx.WritePropString("GlobalSettings");
                fbx.BeginNode("Count"); fbx.WritePropInt(1); fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();
                fbx.BeginNode("ObjectType");
                fbx.WritePropString("Model");
                fbx.BeginNode("Count"); fbx.WritePropInt(1); fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();
                fbx.BeginNode("ObjectType");
                fbx.WritePropString("Geometry");
                fbx.BeginNode("Count"); fbx.WritePropInt(1); fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();
                fbx.BeginNode("ObjectType");
                fbx.WritePropString("Material");
                fbx.BeginNode("Count"); fbx.WritePropInt(1); fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();
                fbx.BeginNode("ObjectType");
                fbx.WritePropString("Texture");
                fbx.BeginNode("Count"); fbx.WritePropInt(1); fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();

                // ==================== 6. Objects ====================
                fbx.BeginNode("Objects");

                // ---- Geometry ----
                fbx.BeginNode("Geometry");
                fbx.WritePropLong(geomId);
                fbx.WritePropString("Geometry::" + mesh.name);
                fbx.WritePropString("Mesh");
                fbx.BeginNode("GeometryVersion"); fbx.WritePropInt(124); fbx.EndNode();   // 关键：three.js/ModelScope 必读

                // Vertices: 3 floats per vertex (X 翻转：Unity LH → FBX RH)
                float[] vertArr = new float[vertices.Length * 3];
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertArr[i * 3 + 0] = -vertices[i].x;
                    vertArr[i * 3 + 1] = vertices[i].y;
                    vertArr[i * 3 + 2] = vertices[i].z;
                }
                fbx.BeginNode("Vertices"); fbx.WritePropFloatArray(vertArr); fbx.EndNode();

                // PolygonVertexIndex: 每 3 个 int 一组；反转绕序；最后一个顶点取负 -(idx+1)
                int[] polyArr = new int[triangles.Length];
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    polyArr[i + 0] = triangles[i];
                    polyArr[i + 1] = triangles[i + 2];   // 反转
                    polyArr[i + 2] = -(triangles[i + 1] + 1);  // 末顶点取负
                }
                fbx.BeginNode("PolygonVertexIndex"); fbx.WritePropIntArray(polyArr); fbx.EndNode();

                // LayerElementNormal
                if (hasN)
                {
                    float[] normArr = new float[normals.Length * 3];
                    for (int i = 0; i < normals.Length; i++)
                    {
                        normArr[i * 3 + 0] = -normals[i].x;
                        normArr[i * 3 + 1] = normals[i].y;
                        normArr[i * 3 + 2] = normals[i].z;
                    }
                    fbx.BeginNode("LayerElementNormal"); fbx.WritePropInt(0);
                    fbx.BeginNode("Version"); fbx.WritePropInt(101); fbx.EndNode();
                    fbx.BeginNode("Name"); fbx.WritePropString(""); fbx.EndNode();
                    fbx.BeginNode("MappingInformationType"); fbx.WritePropString("ByVertice"); fbx.EndNode();
                    fbx.BeginNode("ReferenceInformationType"); fbx.WritePropString("Direct"); fbx.EndNode();
                    fbx.BeginNode("Normals"); fbx.WritePropFloatArray(normArr); fbx.EndNode();
                    fbx.WriteNullTerminator();
                    fbx.EndNode();
                }

                // LayerElementUV
                if (hasU)
                {
                    float[] uvArr = new float[uvs.Length * 2];
                    for (int i = 0; i < uvs.Length; i++)
                    {
                        uvArr[i * 2 + 0] = uvs[i].x;
                        uvArr[i * 2 + 1] = uvs[i].y;
                    }
                    fbx.BeginNode("LayerElementUV"); fbx.WritePropInt(0);
                    fbx.BeginNode("Version"); fbx.WritePropInt(101); fbx.EndNode();
                    fbx.BeginNode("Name"); fbx.WritePropString("UVMap"); fbx.EndNode();
                    fbx.BeginNode("MappingInformationType"); fbx.WritePropString("ByVertice"); fbx.EndNode();
                    fbx.BeginNode("ReferenceInformationType"); fbx.WritePropString("Direct"); fbx.EndNode();
                    fbx.BeginNode("UV"); fbx.WritePropFloatArray(uvArr); fbx.EndNode();
                    fbx.WriteNullTerminator();
                    fbx.EndNode();
                }

                // Layer
                fbx.BeginNode("Layer"); fbx.WritePropInt(0);
                fbx.BeginNode("Version"); fbx.WritePropInt(100); fbx.EndNode();
                if (hasN)
                {
                    fbx.BeginNode("LayerElement");
                    fbx.WritePropString("LayerElementNormal");
                    fbx.WritePropInt(0);
                    fbx.WritePropInt(0);
                    fbx.EndNode();
                }
                if (hasU)
                {
                    fbx.BeginNode("LayerElement");
                    fbx.WritePropString("LayerElementUV");
                    fbx.WritePropInt(0);
                    fbx.WritePropInt(0);
                    fbx.EndNode();
                }
                fbx.WriteNullTerminator();
                fbx.EndNode();

                fbx.WriteNullTerminator();
                fbx.EndNode();   // /Geometry

                // ---- Model ----
                fbx.BeginNode("Model");
                fbx.WritePropLong(modelId);
                fbx.WritePropString("Model::" + mesh.name);
                fbx.WritePropString("Mesh");
                fbx.BeginNode("Version"); fbx.WritePropInt(232); fbx.EndNode();
                fbx.BeginNode("Properties70");
                fbx.BeginNode("P");
                fbx.WritePropString("DefaultAttributeIndex");
                fbx.WritePropString("int"); fbx.WritePropString("Integer"); fbx.WritePropString("");
                fbx.WritePropInt(0);
                fbx.EndNode();
                fbx.BeginNode("P");
                fbx.WritePropString("Lcl Translation");
                fbx.WritePropString("Lcl Translation"); fbx.WritePropString(""); fbx.WritePropString("A");
                fbx.WritePropDouble(0); fbx.WritePropDouble(0); fbx.WritePropDouble(0);
                fbx.EndNode();
                fbx.BeginNode("P");
                fbx.WritePropString("Lcl Rotation");
                fbx.WritePropString("Lcl Rotation"); fbx.WritePropString(""); fbx.WritePropString("A");
                fbx.WritePropDouble(0); fbx.WritePropDouble(0); fbx.WritePropDouble(0);
                fbx.EndNode();
                fbx.BeginNode("P");
                fbx.WritePropString("Lcl Scaling");
                fbx.WritePropString("Lcl Scaling"); fbx.WritePropString(""); fbx.WritePropString("A");
                fbx.WritePropDouble(1); fbx.WritePropDouble(1); fbx.WritePropDouble(1);
                fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();
                fbx.BeginNode("Shading"); fbx.WritePropBool(true); fbx.EndNode();    // 关键：three.js 读
                fbx.BeginNode("Culling"); fbx.WritePropString("CullingOff"); fbx.EndNode();   // 关键
                fbx.WriteNullTerminator();
                fbx.EndNode();   // /Model

                // ---- Material (保底) ----
                fbx.BeginNode("Material");
                fbx.WritePropLong(matId);
                fbx.WritePropString("Material::UE_Default");
                fbx.WritePropString("");
                fbx.BeginNode("Version"); fbx.WritePropInt(102); fbx.EndNode();
                fbx.BeginNode("Properties70");
                fbx.BeginNode("P");
                fbx.WritePropString("DiffuseColor");
                fbx.WritePropString("ColorRGB"); fbx.WritePropString("Color"); fbx.WritePropString("");
                fbx.WritePropDouble(0.8); fbx.WritePropDouble(0.8); fbx.WritePropDouble(0.8);
                fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();

                // ---- Texture (保底) ----
                fbx.BeginNode("Texture");
                fbx.WritePropLong(texId);
                fbx.WritePropString("Texture::UE_Default");
                fbx.WritePropString("");
                fbx.BeginNode("Version"); fbx.WritePropInt(202); fbx.EndNode();
                fbx.BeginNode("TextureName"); fbx.WritePropString("Texture::UE_Default"); fbx.EndNode();
                fbx.BeginNode("Properties70");
                fbx.BeginNode("P");
                fbx.WritePropString("UVSet");
                fbx.WritePropString("KString"); fbx.WritePropString(""); fbx.WritePropString("");
                fbx.WritePropString("UVMap");
                fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();

                fbx.WriteNullTerminator();
                fbx.EndNode();   // /Objects

                // ==================== 7. Connections ====================
                // FBX 7.5 binary 中 C 连接编码为 3 个属性：type(S) + src(L) + dst(L)
                fbx.BeginNode("Connections");
                // doc → world (0)
                fbx.WritePropString("OO"); fbx.WritePropLong(docId); fbx.WritePropLong(0);
                // root → doc
                fbx.WritePropString("OO"); fbx.WritePropLong(rootId); fbx.WritePropLong(docId);
                // geom → model
                fbx.WritePropString("OO"); fbx.WritePropLong(geomId); fbx.WritePropLong(modelId);
                // model → root
                fbx.WritePropString("OO"); fbx.WritePropLong(modelId); fbx.WritePropLong(rootId);
                // mat → model
                fbx.WritePropString("OO"); fbx.WritePropLong(matId); fbx.WritePropLong(modelId);
                // tex → mat
                fbx.WritePropString("OO"); fbx.WritePropLong(texId); fbx.WritePropLong(matId);
                fbx.EndNode();

                // ==================== 8. Takes ====================
                fbx.BeginNode("Takes");
                fbx.BeginNode("Current"); fbx.WritePropString(""); fbx.EndNode();
                fbx.WriteNullTerminator();
                fbx.EndNode();

                // 文件终止：25 个零字节
                fbx.WriteBytes(new byte[25]);

                File.WriteAllBytes(path, fbx.ToArray());
                ExplorerCore.Log($"FBX 已导出 (binary 7.5): {path} ({fbx.Length} 字节)");
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"导出 FBX 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Binary FBX 7.5 写入器。基于 MemoryStream + BinaryWriter (LE)。
        /// 节点 header 13 字节：end_off(u32) + num_props(u32) + prop_len(u32) + name_len(u8)。
        /// 嵌套子节点结尾追加 13 个零字节（null terminator）。
        /// </summary>
        private class FBXBinaryWriter
        {
            private readonly MemoryStream _ms = new MemoryStream();
            private readonly BinaryWriter _bw;
            private readonly Stack<long> _nodeStart = new Stack<long>();   // header 起点
            private readonly Stack<long> _propStart = new Stack<long>();   // 属性数据起点
            private readonly Stack<int>  _propCount = new Stack<int>();    // 已写入属性数
            private readonly Stack<long> _childrenStart = new Stack<long>(); // 第一个 child 起点（-1 = 哨兵无 children）

            public FBXBinaryWriter()
            {
                _bw = new BinaryWriter(_ms);
            }

            public int Length => (int)_ms.Length;

            public byte[] ToArray() => _ms.ToArray();

            // ---- Low-level writes ----
            public void WriteBytes(byte[] data) { _bw.Write(data); }
            public void WriteU32(uint v) { _bw.Write(v); }
            public void WriteU8(byte v) { _bw.Write(v); }

            // ---- Property encoders (1B type tag + N bytes value) ----
            public void WritePropBool(bool v)
            {
                _bw.Write((byte)'Y');
                _bw.Write(v ? (byte)1 : (byte)0);
                _propCount.Push(_propCount.Pop() + 1);
            }

            public void WritePropInt(int v)
            {
                _bw.Write((byte)'I');
                _bw.Write(v);
                _propCount.Push(_propCount.Pop() + 1);
            }

            public void WritePropLong(long v)
            {
                _bw.Write((byte)'L');
                _bw.Write(v);
                _propCount.Push(_propCount.Pop() + 1);
            }

            public void WritePropFloat(float v)
            {
                _bw.Write((byte)'F');
                _bw.Write(v);
                _propCount.Push(_propCount.Pop() + 1);
            }

            public void WritePropDouble(double v)
            {
                _bw.Write((byte)'D');
                _bw.Write(v);
                _propCount.Push(_propCount.Pop() + 1);
            }

            public void WritePropIntArray(int[] arr)
            {
                _bw.Write((byte)'i');
                _bw.Write((uint)arr.Length);   // FBX: u32 count (元素个数, not 字节数)
                for (int i = 0; i < arr.Length; i++) _bw.Write(arr[i]);
                _propCount.Push(_propCount.Pop() + 1);
            }

            public void WritePropFloatArray(float[] arr)
            {
                _bw.Write((byte)'f');
                _bw.Write((uint)arr.Length);
                for (int i = 0; i < arr.Length; i++) _bw.Write(arr[i]);
                _propCount.Push(_propCount.Pop() + 1);
            }

            public void WritePropLongArray(long[] arr)
            {
                _bw.Write((byte)'l');
                _bw.Write((uint)arr.Length);
                for (int i = 0; i < arr.Length; i++) _bw.Write(arr[i]);
                _propCount.Push(_propCount.Pop() + 1);
            }

            public void WritePropDoubleArray(double[] arr)
            {
                _bw.Write((byte)'d');
                _bw.Write((uint)arr.Length);
                for (int i = 0; i < arr.Length; i++) _bw.Write(arr[i]);
                _propCount.Push(_propCount.Pop() + 1);
            }

            public void WritePropString(string s)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(s);
                _bw.Write((byte)'S');
                _bw.Write((uint)utf8.Length);
                _bw.Write(utf8);
                _propCount.Push(_propCount.Pop() + 1);
            }

            // ---- Node building ----
            public void BeginNode(string name)
            {
                // 1) 若当前父节点尚未记录 childrenStart（哨兵 -1），记下当前位置作为 children 起点
                if (_childrenStart.Count > 0 && _childrenStart.Peek() == -1)
                {
                    _childrenStart.Pop();
                    _childrenStart.Push(_ms.Position);
                }

                long startPos = _ms.Position;
                _nodeStart.Push(startPos);

                // header 占位 4+4+4 = 12 字节
                _bw.Write((uint)0);   // end_off
                _bw.Write((uint)0);   // num_props
                _bw.Write((uint)0);   // prop_len

                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                if (nameBytes.Length > 255)
                    throw new System.Exception($"FBX node name too long: {name}");
                _bw.Write((byte)nameBytes.Length);
                _bw.Write(nameBytes);

                _propStart.Push(_ms.Position);
                _propCount.Push(0);
                _childrenStart.Push(-1);  // 哨兵：当前节点尚未有 children
            }

            public void EndNode()
            {
                long startPos    = _nodeStart.Pop();
                long propStart   = _propStart.Pop();
                int  propCnt     = _propCount.Pop();
                long childrenPos = _childrenStart.Pop();

                long endPos = _ms.Position;   // 此时在所有子节点 + null term 之后
                long endOff  = endPos - startPos;   // absolute offset from start
                // prop_len 只算 properties，不算 children（与 three.js 一致）
                long propsEnd  = (childrenPos >= 0) ? childrenPos : endPos;
                long propLen   = propsEnd - propStart;

                // end_off = actual size (three.js 看到 end_off=0 会跳过整个 record)
                _ms.Position = startPos;
                _bw.Write((uint)endOff);              // end_off = absolute size
                // patch num_props (offset 4)
                _ms.Position = startPos + 4;
                _bw.Write((uint)propCnt);
                // patch prop_len (offset 8)
                _ms.Position = startPos + 8;
                _bw.Write((uint)propLen);
                // 流指针恢复
                _ms.Position = endPos;
            }

            public void WriteNullTerminator()
            {
                _bw.Write(new byte[13]);
            }
        }

        /// <summary>
        /// 导出音频为 WAV
        /// </summary>
        public static void ExportAudioAsWAV(AudioClip clip, string path)
        {
            if (!clip)
            {
                ExplorerCore.LogWarning("音频为空，无法导出！");
                return;
            }

            if (!path.EndsWith(".wav", System.StringComparison.OrdinalIgnoreCase))
                path += ".wav";

            path = EnsureValidPath(path);

            // v16ar：首个音频导出前跑一次读写自检（全局只跑一次）
            AudioReadSelfTest();

            try
            {
                // v16ap：改为跨后端安全读取（原实现直接传 float[]，在 IL2CPP 下会静默得到全 0）
                float[] samples;
                string why;
                if (!TryGetClipSamples(clip, out samples, out why))
                {
                    ExplorerCore.LogWarning($"音频数据读取失败({why})，未生成文件: {clip.name}");
                    return;
                }

                // 静音自检：全 0 时给出明确警告（仍写出文件，因为真·静音素材也存在）
                int peak = 0;
                for (int i = 0; i < samples.Length; i++)
                {
                    int v = (int)(samples[i] * 32767f);
                    if (v < 0) v = -v;
                    if (v > peak) peak = v;
                }
                if (peak == 0)
                    ExplorerCore.LogWarning($"音频 {clip.name} 采样全为 0（疑似静音素材或未成功解码）");

                // 转换为 16-bit PCM
                short[] intData = new short[samples.Length];
                byte[] bytesData = new byte[samples.Length * 2];
                for (int i = 0; i < samples.Length; i++)
                {
                    intData[i] = (short)(samples[i] * 32767f);
                    byte[] byteArr = System.BitConverter.GetBytes(intData[i]);
                    byteArr.CopyTo(bytesData, i * 2);
                }

                using (System.IO.FileStream fs = new System.IO.FileStream(path, System.IO.FileMode.Create))
                {
                    // WAV header
                    byte[] riff = System.Text.Encoding.UTF8.GetBytes("RIFF");
                    fs.Write(riff, 0, 4);
                    byte[] chunkSize = System.BitConverter.GetBytes(36 + bytesData.Length);
                    fs.Write(chunkSize, 0, 4);
                    byte[] wave = System.Text.Encoding.UTF8.GetBytes("WAVE");
                    fs.Write(wave, 0, 4);
                    byte[] fmt = System.Text.Encoding.UTF8.GetBytes("fmt ");
                    fs.Write(fmt, 0, 4);
                    byte[] subChunk1 = System.BitConverter.GetBytes(16);
                    fs.Write(subChunk1, 0, 4);
                    byte[] audioFormat = System.BitConverter.GetBytes((ushort)1);
                    fs.Write(audioFormat, 0, 2);
                    byte[] numChannels = System.BitConverter.GetBytes((ushort)clip.channels);
                    fs.Write(numChannels, 0, 2);
                    byte[] sampleRate = System.BitConverter.GetBytes(clip.frequency);
                    fs.Write(sampleRate, 0, 4);
                    byte[] byteRate = System.BitConverter.GetBytes(clip.frequency * clip.channels * 2);
                    fs.Write(byteRate, 0, 4);
                    byte[] blockAlign = System.BitConverter.GetBytes((ushort)(clip.channels * 2));
                    fs.Write(blockAlign, 0, 2);
                    byte[] bitsPerSample = System.BitConverter.GetBytes((ushort)16);
                    fs.Write(bitsPerSample, 0, 2);
                    byte[] data = System.Text.Encoding.UTF8.GetBytes("data");
                    fs.Write(data, 0, 4);
                    byte[] subChunk2 = System.BitConverter.GetBytes(bytesData.Length);
                    fs.Write(subChunk2, 0, 4);
                    // PCM data
                    fs.Write(bytesData, 0, bytesData.Length);
                }

                ExplorerCore.Log($"音频已导出: {path}");
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"导出音频失败（可能是压缩或流式音频）: {ex.Message}");
            }
        }

        /// <summary>
        /// v16at：该 AudioClip 是否由 PCMReaderCallback 驱动（= 流式 / 中间件桥接音频）。
        ///
        /// ★ 为什么这是关键判据：Unity 官方 issue UUM-363
        ///   「Cannot get data from streamed samples when using PCM read callbacks」明确说明
        ///   **GetData 对这种 clip 永远取不到数据**，而且失败时**返回值仍是 true**
        ///   （Unity 自己在 issue 备注里承认 GetData 的返回值没有文档化）。
        ///   2019.4.X 被标记为 Won't Fix。
        ///   所以「导出文件体积正常但内容全 0 + SoundManager.cpp unlock 报错」是**官方行为**，
        ///   不是调用姿势错误，也不是某个游戏的偶发问题 —— 凡是把 FMOD / Wwise / CRI 等
        ///   中间件音频用 AudioClip.Create(..., PCMReaderCallback) 桥接进 Unity 的游戏都会中招。
        /// </summary>
        private static bool ClipHasPCMReaderCallback(AudioClip clip)
        {
            try
            {
                System.Reflection.PropertyInfo p = typeof(AudioClip).GetProperty(
                    "m_PCMReaderCallback",
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance);
                if (p == null) return false;
                object cb = p.GetValue(clip, null);
                return cb != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// v16at：直接调用 AudioClip 的 PCMReaderCallback 取回全量 PCM。
        ///
        /// 原理：这个委托就是 Unity 平时喂 PCM 给音频线程的同一条通路
        ///   （原生侧走 AudioClip::InvokePCMReaderCallback_Internal → m_PCMReaderCallback(data)）。
        ///   它是**托管委托**，interop 已经把它暴露成可读属性，于是我们可以自己调它：
        ///   不用真的播放、不用等实时、不用 ClassInjector 注入 MonoBehaviour。
        ///
        /// 块大小的选择（两种都试，因为流式与非流式的契约不同）：
        ///   · 流式 clip  ：Unity 用 DSP 缓冲（默认 1024 帧）反复调，回调内部自己维护读指针。
        ///   · 非流式 clip：Unity 用「全长 × 声道数」的缓冲调一次。
        ///   先按 1024 帧分块（贴合流式契约），再退化到全长一次。
        ///
        /// 副作用处理：把流抽干后读指针会停在末尾，会影响之后游戏里的播放。
        ///   读完用 m_PCMSetPositionCallback(0) 倒回流首（这正是 AudioSource.timeSamples 的机制），
        ///   拿不到该回调时只告警，不影响导出结果。
        /// </summary>
        private static bool TryReadClipViaPCMCallback(AudioClip clip, out float[] samples, out string why)
        {
            samples = null;
            why = null;
            try
            {
                int channels = clip.channels;
                long frameCount = clip.samples;
                long total = frameCount * channels;
                if (channels <= 0 || frameCount <= 0) { why = $"samples={frameCount} channels={channels}"; return false; }
                if (total > 64L * 1024 * 1024) { why = "采样数异常过大(" + total + ")"; return false; }

                // ---- 1. 取委托 ----
                System.Reflection.PropertyInfo cbProp = typeof(AudioClip).GetProperty(
                    "m_PCMReaderCallback",
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance);
                if (cbProp == null) { why = "该后端不暴露 m_PCMReaderCallback"; return false; }

                object cb = null;
                try { cb = cbProp.GetValue(clip, null); } catch { }
                if (cb == null) { why = "m_PCMReaderCallback 为 null（回调注册在 native 侧）"; return false; }

                // ---- 2. 找 Invoke 及其缓冲区类型 ----
                System.Reflection.MethodInfo invoke = null;
                System.Type bufType = null;
                foreach (System.Reflection.MethodInfo m in cb.GetType().GetMethods(
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (m.Name != "Invoke") continue;
                    System.Reflection.ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    System.Type pt = ps[0].ParameterType;
                    if (pt == typeof(float[]) || pt.IsArray)
                    {
                        invoke = m; bufType = pt; break;
                    }
                }
                if (invoke == null) { why = "回调类型没有 Invoke(数组)"; return false; }

                // ---- 3. 两种块大小各试一次 ----
                //   先 1024 帧（Unity 自己的 DSP 缓冲尺寸，流式 clip 的真实契约），
                //   再退化到「全长一次」（兼容按一次调用灌满整段的实现）。
                //   顺序不能反：流式回调对「全长一次」往往只填第一块就不管了，
                //   剩下全 0 却仍满足 peak>0，会静默产出一个只有开头有声的假文件。
                long[] blockFrames = new long[] { 1024, 0 };   // 0 表示「全长一次」
                string lastWhy = null;
                for (int bi = 0; bi < blockFrames.Length; bi++)
                {
                    long blk = blockFrames[bi] == 0 ? frameCount : blockFrames[bi];
                    float[] outBuf = new float[total];
                    long done = 0;
                    bool threw = false;

                    while (done < frameCount)
                    {
                        long n = System.Math.Min(blk, frameCount - done);
                        object arr = NewIl2CppFloatArray(bufType, n * channels);
                        if (arr == null) { lastWhy = "无法构造回调缓冲区(" + bufType.Name + ")"; threw = true; break; }
                        try { invoke.Invoke(cb, new object[] { arr }); }
                        catch (System.Exception e2) { lastWhy = "回调异常: " + ExMsg(e2); threw = true; break; }

                        if (!CopyFloatArrayBack(bufType, arr, outBuf, done * channels))
                        {
                            lastWhy = "回调缓冲区拷回失败";
                            threw = true;
                            break;
                        }
                        done += n;
                        if (blockFrames[bi] == 0) break;   // 全长模式只调一次
                    }

                    if (threw) continue;

                    RewindClipStream(clip);       // 抽干后倒回流首，避免影响游戏内播放

                    int peak = PeakInt16(outBuf);
                    ExplorerCore.Log($"[音频] {clip.name} PCMReaderCallback 读取: " +
                                     $"块={(blockFrames[bi] == 0 ? "全长一次" : blockFrames[bi] + "帧×" + ((frameCount + 1023) / 1024) + "次")} " +
                                     $"frames={frameCount} ch={channels} 峰值={peak}");
                    if (peak > 0) { samples = outBuf; why = null; return true; }
                    lastWhy = "调用成功但数据仍为 0（块=" + (blockFrames[bi] == 0 ? "全长" : "1024帧") + "）";
                }

                why = lastWhy ?? "未取到数据";
                return false;
            }
            catch (System.Exception e)
            {
                why = ExMsg(e);
                return false;
            }
        }

        /// <summary>把流式读指针倒回 0（拿不到该回调时静默跳过）。</summary>
        private static void RewindClipStream(AudioClip clip)
        {
            try
            {
                System.Reflection.PropertyInfo p = typeof(AudioClip).GetProperty(
                    "m_PCMSetPositionCallback",
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance);
                if (p == null) return;
                object cb = p.GetValue(clip, null);
                if (cb == null) { ExplorerCore.LogWarning("[音频] 无 m_PCMSetPositionCallback，流指针停留在末尾（该 clip 下次播放可能异常）"); return; }
                foreach (System.Reflection.MethodInfo m in cb.GetType().GetMethods(
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (m.Name != "Invoke") continue;
                    System.Reflection.ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    System.Type pt = ps[0].ParameterType;
                    if (pt != typeof(int) && pt != typeof(short) && pt != typeof(long)) continue;
                    object arg = pt == typeof(int) ? (object)0 : pt == typeof(short) ? (object)(short)0 : (object)0L;
                    try { m.Invoke(cb, new object[] { arg }); } catch { }
                    return;
                }
            }
            catch { }
        }

        /// <summary>按 il2cpp 数组类型构造 float 缓冲区（构造重载按参数类型挑 long / int）。</summary>
        private static object NewIl2CppFloatArray(System.Type arrType, long len)
        {
            try
            {
                foreach (System.Reflection.ConstructorInfo c in arrType.GetConstructors())
                {
                    System.Reflection.ParameterInfo[] cp = c.GetParameters();
                    if (cp.Length != 1) continue;
                    System.Type pt = cp[0].ParameterType;
                    if (pt != typeof(long) && pt != typeof(int)) continue;
                    try
                    {
                        object arg = pt == typeof(long) ? (object)len : (object)(int)len;
                        object a = c.Invoke(new object[] { arg });
                        if (a != null) return a;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>把 il2cpp float 缓冲区拷回托管数组的指定位置（优先 CopyTo(float[],int)，退化用索引器）。</summary>
        private static bool CopyFloatArrayBack(System.Type arrType, object arr, float[] dst, long dstOffset)
        {
            try
            {
                if (arr == null) return false;
                if (arrType == typeof(float[])) { ((float[])arr).CopyTo(dst, (int)dstOffset); return true; }

                long len = ArrayLengthOf2(arr);
                System.Reflection.MethodInfo copyTo = arrType.GetMethod(
                    "CopyTo", new[] { typeof(float[]), typeof(int) });
                if (copyTo != null && len > 0)
                {
                    copyTo.Invoke(arr, new object[] { dst, (int)dstOffset });
                    return true;
                }
                System.Reflection.PropertyInfo item = arrType.GetProperty("Item");
                if (item == null) return false;
                if (len <= 0) len = dst.Length - dstOffset;
                for (long i = 0; i < len && (dstOffset + i) < dst.Length; i++)
                    dst[dstOffset + i] = System.Convert.ToSingle(item.GetValue(arr, new object[] { (int)i }));
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 读取 AudioClip 采样（多通道，按「代价从小到大」排序）。
        ///
        /// v16aq：带自愈 —— 读到全 0 时 UnloadAudioData + LoadAudioData 强制从磁盘重载后再读一次。
        /// v16at：新增「PCMReaderCallback 直调」通道 —— 流式/中间件桥接 clip 的唯一出路。
        /// v16ax：**顺序调整，bank 直读提到最前**。
        ///   实机依据（HTRHGF 17:19，v16aw）：日志里 `AudioClip.GetData` 被调用 18 次
        ///   （9 个 clip × 「初读 + 强制卸载/重载后重读」各 1 次），Player.log 里**恰好** 18 条
        ///   `SoundManager.cpp(729) unlock(...) (An invalid parameter was passed…)`，一一对应。
        ///   Unity 对**磁盘流式** clip 的 GetData 是 lock 成功 / unlock 失败 → 「返回 true + 缓冲全 0」。
        ///   也就是说这条通道对这些游戏**注定**失败，唯一效果是刷满原生错误日志，
        ///   外加 `UnloadAudioData/LoadAudioData` 扰动正在播放的音频。能不用就不用。
        ///
        /// 安全性（为什么敢把 bank 提到最前）：bank 命中必须通过
        ///   `AudioBankExporter.TryReadFromResourceBank` 里的**元信息硬校验**
        ///   （channels / frequency / frames 与 clip 精确相等），对位错的 bank 会被拒绝并自动回落，
        ///   不会出现「静默导出别的音频」这种比静音更糟的结果。
        ///
        /// 通道 ② GetData 仍完整保留：bank 不可用（非流式 clip / 对位失败 / 未附带 fmod.dll）时
        ///   它是唯一出路；而且有些非流式 clip 只是内存副本没灌好，卸载重载后确实能读出来。
        /// 通道 ③ PCMReaderCallback：bank 与 GetData 都没数据时的最后出路。
        /// </summary>
        private static bool TryGetClipSamples(AudioClip clip, out float[] samples, out string why)
        {
            samples = null;
            why = null;
            try
            {
                float[] fallback = null;    // 所有通道都拿不到非静音数据时的兜底（文件照写，上层打告警）

                // 先保证 clip 数据/元信息可用，**再**走 bank 通道。
                //   ★ 顺序要点：`EnsureClipDataLoaded` 必须排在 bank 通道之前 ——
                //   否则 clip 未加载时 `clip.samples` 可能为 0，会让 bank 的元信息硬校验误判成
                //   「对位错误」而白白拒掉正确结果。（该方法只调 `LoadAudioData`，
                //   **不会**触发 GetData，因此不产生任何 SoundManager 错误。）
                LogClipDiag(clip, "初始");
                EnsureClipDataLoaded(clip);

                // ---------- 通道 ①（v16ax，提到最前）：FSB5 音频 bank 直读 ----------
                //   适用于「音频以 FSB5 bank 存在 .resource 里、由 Unity 走**磁盘流式**加载」的游戏。
                //   这类 clip 的 GetData **注定**失败：Unity 官方文档明写
                //   "GetData doesn't work with streamed audio clips, including clips streamed from the disk"，
                //   官方 issue UUM-363 在 2019.4/2020.3/2021.2 判定 Won't Fix，只补记了
                //   「GetData 的返回值未文档化」—— 这就是「读不到却返回 true + 缓冲全 0」的出处。
                //   本通道绕开 AudioClip API，直接读游戏自己的 .resource，用随插件附带的
                //   fmod.dll 解成 PCM。详见 AudioBankExporter 的类注释。
                {
                    float[] bankBuf;
                    string bankWhy;
                    if (AudioBankExporter.TryReadFromResourceBank(clip, out bankBuf, out bankWhy))
                    {
                        if (PeakInt16(bankBuf) > 0) { samples = bankBuf; return true; }
                        ExplorerCore.LogWarning($"[音频] {clip.name} bank 通道解出全 0（{bankWhy}）");
                        fallback = bankBuf;
                    }
                    else
                    {
                        ExplorerCore.LogWarning($"[音频] {clip.name} bank 通道未命中: {bankWhy}");
                    }
                }

                // ---------- 通道 ②（原通道 ①）：AudioClip.GetData（带「卸载/重载后再读」自愈）----------
                //   注意：这里**不再**重复调用 EnsureClipDataLoaded —— 上面已经调过了。
                float[] buf = null;
                if (TryReadClipBuffer(clip, out buf, out why))
                {
                    int peak = PeakInt16(buf);
                    if (peak > 0) { samples = buf; return true; }

                    // 全 0：强制卸载 + 重新加载后重读
                    ExplorerCore.LogWarning($"[音频] {clip.name} 首次读取全为 0，强制卸载/重载后再读…");
                    ForceReloadClip(clip);
                    LogClipDiag(clip, "重载后");

                    float[] buf2;
                    string why2;
                    if (TryReadClipBuffer(clip, out buf2, out why2))
                    {
                        int peak2 = PeakInt16(buf2);
                        ExplorerCore.Log($"[音频] {clip.name} 重读结果: 峰值={peak2}");
                        if (peak2 > 0) { samples = buf2; return true; }
                        buf = buf2;
                    }
                }
                if (fallback == null) fallback = buf;   // GetData 是最后的兜底来源

                // ---------- 通道 ③（v16at）：PCMReaderCallback 直调 ----------
                //   bank / GetData 都出不来数据时才走这里。若是「用 PCM 读回调的流式 clip」，
                //   GetData 按 Unity 官方口径（UUM-363）永远失败且返回 true，
                //   此时唯一的读取方式就是这个回调本身。
                if (ClipHasPCMReaderCallback(clip))
                {
                    ExplorerCore.Log($"[音频] {clip.name} 检测到 PCMReaderCallback（流式/中间件桥接）→ 改走回调直调通道");
                    float[] cbBuf;
                    string cbWhy;
                    if (TryReadClipViaPCMCallback(clip, out cbBuf, out cbWhy))
                    {
                        // 回调通道拿到非静音数据 → 优先采用
                        if (PeakInt16(cbBuf) > 0) { samples = cbBuf; return true; }
                        ExplorerCore.LogWarning($"[音频] {clip.name} 回调通道数据仍为 0（{cbWhy}）");
                        if (fallback == null) fallback = cbBuf;
                    }
                    else
                    {
                        ExplorerCore.LogWarning($"[音频] {clip.name} 回调通道失败: {cbWhy}");
                    }
                }

                if (fallback == null) { why = why ?? "所有通道均未取到数据"; return false; }
                samples = fallback;     // 真·静音素材：交给上层打告警，文件照写
                return true;
            }
            catch (System.Exception e)
            {
                why = ExMsg(e);
                return false;
            }
        }

        /// <summary>缓冲区峰值（转 16bit 后，用于静音判定）。</summary>
        private static int PeakInt16(float[] buf)
        {
            if (buf == null) return 0;
            int peak = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                int v = (int)(buf[i] * 32767f);
                if (v < 0) v = -v;
                if (v > peak) peak = v;
            }
            return peak;
        }

        /// <summary>打印音频诊断信息（samples/channels/frequency/loadState/loadType）。</summary>
        private static void LogClipDiag(AudioClip clip, string tag)
        {
            try
            {
                string ls = "?", lt = "?";
                System.Reflection.PropertyInfo p = typeof(AudioClip).GetProperty("loadState");
                if (p != null) { try { ls = System.Convert.ToString(p.GetValue(clip, null)); } catch { } }
                p = typeof(AudioClip).GetProperty("loadType");
                if (p != null) { try { lt = System.Convert.ToString(p.GetValue(clip, null)); } catch { } }
                ExplorerCore.Log($"[音频] {clip.name} {tag}: samples={clip.samples} channels={clip.channels} " +
                                 $"freq={clip.frequency} loadState={ls} loadType={lt}");
            }
            catch { }
        }

        /// <summary>强制卸载再加载音频数据（让 native 重新从磁盘读整段数据）。</summary>
        private static void ForceReloadClip(AudioClip clip)
        {
            try
            {
                System.Reflection.MethodInfo un = typeof(AudioClip).GetMethod(
                    "UnloadAudioData", BindingFlags.Public | BindingFlags.Instance);
                if (un != null) { try { un.Invoke(clip, null); } catch { } }

                System.Reflection.MethodInfo load = typeof(AudioClip).GetMethod(
                    "LoadAudioData", BindingFlags.Public | BindingFlags.Instance);
                if (load != null) { try { load.Invoke(clip, null); } catch { } }

                System.Reflection.PropertyInfo ls = typeof(AudioClip).GetProperty("loadState");
                if (ls == null) return;
                for (int i = 0; i < 60; i++)
                {
                    string st = null;
                    try { st = System.Convert.ToString(ls.GetValue(clip, null)); } catch { }
                    if (st == "Loaded" || st == "Failed") break;
                    System.Threading.Thread.Sleep(50);
                }
            }
            catch { }
        }

        /// <summary>
        /// v16ap：跨后端读取 AudioClip 采样数据（单次，不做自愈）。
        ///
        /// 两个 IL2CPP 独有的坑（本方法都绕开了）：
        /// ① 音频未加载：Streaming / CompressedInMemory 的 clip 默认 loadState != Loaded，
        ///    此时 GetData 会失败（Unity 打印 SoundManager.cpp unlock 错误）并保持缓冲全 0。
        /// ② 数组参数被「拷贝」：IL2CPP 下 GetData 的形参是 Il2CppStructArray&lt;float&gt;，
        ///    直接把托管 float[] 传进去只会隐式转换成一份临时 il2cpp 数组，数据填进临时数组后即被丢弃，
        ///    托管数组永远全 0（文件体积正常但内容静音，最难发现）。
        ///    → 按形参类型显式构造缓冲区，再用 CopyTo/索引器拷回托管数组。
        /// </summary>
        private static bool TryReadClipBuffer(AudioClip clip, out float[] samples, out string why)
        {
            samples = null;
            why = null;
            try
            {
                long total = (long)clip.samples * clip.channels;
                if (total <= 0) { why = $"samples={clip.samples} channels={clip.channels}"; return false; }
                if (total > 64L * 1024 * 1024) { why = "采样数异常过大(" + total + ")"; return false; }

                // 找 GetData(?, int)
                System.Reflection.MethodInfo gd = null;
                foreach (System.Reflection.MethodInfo m in typeof(AudioClip).GetMethods(
                             BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "GetData") continue;
                    System.Reflection.ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 2 && ps[1].ParameterType == typeof(int)) { gd = m; break; }
                }
                if (gd == null) { why = "找不到 GetData"; return false; }

                System.Type p0 = gd.GetParameters()[0].ParameterType;
                float[] outBuf = new float[total];

                // Mono / 原生 float[] 形参：直接传
                if (p0 == typeof(float[]))
                {
                    object r = gd.Invoke(clip, new object[] { outBuf, 0 });
                    if (!IsTrueObj(r)) { why = "GetData 返回 false"; return false; }
                    samples = outBuf;
                    return true;
                }

                // IL2CPP：显式构造 il2cpp 数组（构造重载按参数类型挑 long / int）
                object arr = null;
                foreach (System.Reflection.ConstructorInfo c in p0.GetConstructors())
                {
                    System.Reflection.ParameterInfo[] cp = c.GetParameters();
                    if (cp.Length != 1) continue;
                    System.Type pt = cp[0].ParameterType;
                    if (pt != typeof(long) && pt != typeof(int)) continue;
                    try
                    {
                        object arg = pt == typeof(long) ? (object)total : (object)(int)total;
                        arr = c.Invoke(new object[] { arg });
                        if (arr != null) break;
                    }
                    catch { }
                }
                if (arr == null) { why = "无法构造缓冲区类型 " + p0.Name; return false; }

                // v16ar 诊断：确认缓冲区长度是否真的等于 samples*channels
                // （长度不符会让 Unity 内部 lock/unlock 的 size 对不上 → SoundManager unlock 报错 + 数据全 0）
                long arrLen = ArrayLengthOf2(arr);
                if (arrLen != total)
                    ExplorerCore.LogWarning($"[音频] {clip.name} 缓冲区长度异常: 构造={total} 实际={arrLen}");

                object ret = gd.Invoke(clip, new object[] { arr, 0 });
                bool ok = IsTrueObj(ret);

                // v16ar 诊断：直接从 il2cpp 缓冲区抽样读峰值（区分「native 没写数据」和「我们拷回失败」）
                int arrPeak = SamplePeakFromArray(p0, arr, 4096);

                if (!ok)
                {
                    why = "GetData 返回 false（loadState=" + GetClipLoadStateText(clip) + "）";
                    ExplorerCore.LogWarning($"[音频] {clip.name} GetData=false arrLen={arrLen} arr抽样峰值={arrPeak}");
                    return false;
                }

                // 拷回托管数组：优先 CopyTo(T[], int)，退化用索引器
                System.Reflection.MethodInfo copyTo = p0.GetMethod(
                    "CopyTo", new[] { typeof(float[]), typeof(int) });
                if (copyTo != null)
                    copyTo.Invoke(arr, new object[] { outBuf, 0 });
                else
                {
                    System.Reflection.PropertyInfo item = p0.GetProperty("Item");
                    if (item == null) { why = "缓冲区无 CopyTo/索引器，无法取回数据"; return false; }
                    for (long i = 0; i < total; i++)
                        outBuf[i] = System.Convert.ToSingle(item.GetValue(arr, new object[] { (int)i }));
                }

                // v16ar 诊断汇总
                ExplorerCore.Log($"[音频] {clip.name} 读取详情: arrLen={arrLen} GetData={ok} " +
                                 $"缓冲区抽样峰值={arrPeak} 拷回后峰值={PeakInt16(outBuf)}");

                samples = outBuf;
                return true;
            }
            catch (System.Exception e)
            {
                why = ExMsg(e);
                return false;
            }
        }

        /// <summary>取对象的 Length 属性（跨后端安全）。</summary>
        private static long ArrayLengthOf2(object arr)
        {
            if (arr == null) return -1;
            try
            {
                System.Reflection.PropertyInfo p = arr.GetType().GetProperty("Length");
                if (p != null) return System.Convert.ToInt64(p.GetValue(arr, null));
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// 从 il2cpp 数组等距抽样读若干元素的峰值（不依赖 CopyTo，用于诊断）。
        /// 必须跨全曲抽样：语音素材开头常是静音，只看前 N 个元素会误判为「native 没写数据」。
        /// </summary>
        private static int SamplePeakFromArray(System.Type p0, object arr, int count)
        {
            try
            {
                System.Reflection.PropertyInfo item = p0.GetProperty("Item");
                if (item == null || arr == null) return -1;
                long len = ArrayLengthOf2(arr);
                if (len <= 0) return -1;
                int n = (int)System.Math.Min((long)count, len);
                long stride = System.Math.Max(1L, len / n);
                int peak = 0;
                object[] idx = new object[1];
                for (long i = 0; i < len; i += stride)
                {
                    idx[0] = (int)i;
                    float v = System.Convert.ToSingle(item.GetValue(arr, idx));
                    int iv = (int)(v * 32767f);
                    if (iv < 0) iv = -iv;
                    if (iv > peak) peak = iv;
                }
                return peak;
            }
            catch { return -1; }
        }

        /// <summary>
        /// v16ar 自检：造一段已知音频（440Hz 正弦）→ SetData 写入 → GetData 读回，验证读写机制本身是否可用。
        /// 用来区分「我们的调用方式有问题」和「该游戏的 AudioClip 数据读不出来」。
        /// </summary>
        private static bool _audioSelfTestDone;
        private static void AudioReadSelfTest()
        {
            if (_audioSelfTestDone) return;
            _audioSelfTestDone = true;
            try
            {
                System.Reflection.MethodInfo create = null;
                foreach (System.Reflection.MethodInfo m in typeof(AudioClip).GetMethods(
                             BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "Create") continue;
                    System.Reflection.ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 5 && ps[0].ParameterType == typeof(string)) { create = m; break; }
                }
                if (create == null) { ExplorerCore.Log("[音频自检] AudioClip.Create 不可用，跳过"); return; }

                const int n = 44100;
                AudioClip tmp = create.Invoke(null, new object[] { "UE_AudioSelfTest", n, 1, 44100, false }) as AudioClip;
                if (tmp == null) { ExplorerCore.Log("[音频自检] 创建临时 clip 失败"); return; }

                float[] src = new float[n];
                for (int i = 0; i < n; i++)
                    src[i] = 0.5f * (float)System.Math.Sin(2.0 * System.Math.PI * 440.0 * i / 44100.0);

                // SetData(数组, int)：数组是「进」方向，用 .ctor(float[]) 直接复制进 il2cpp 数组
                System.Reflection.MethodInfo setData = null;
                foreach (System.Reflection.MethodInfo m in typeof(AudioClip).GetMethods(
                             BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "SetData") continue;
                    System.Reflection.ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 2 && ps[1].ParameterType == typeof(int)) { setData = m; break; }
                }
                if (setData == null) { ExplorerCore.Log("[音频自检] 无 SetData，跳过"); return; }

                System.Type sp0 = setData.GetParameters()[0].ParameterType;
                object arrIn;
                if (sp0 == typeof(float[])) arrIn = src;
                else
                {
                    System.Reflection.ConstructorInfo cc = sp0.GetConstructor(new[] { typeof(float[]) });
                    arrIn = cc != null ? cc.Invoke(new object[] { src }) : null;
                }
                if (arrIn == null) { ExplorerCore.Log("[音频自检] 无法构造 SetData 缓冲区"); return; }
                setData.Invoke(tmp, new object[] { arrIn, 0 });

                // 读回
                float[] back;
                string why;
                bool ok = TryReadClipBuffer(tmp, out back, out why);
                int peak = ok ? PeakInt16(back) : -1;
                ExplorerCore.Log($"[音频自检] 440Hz 正弦往返测试: ok={ok} why={why} 读回峰值={peak} " +
                                 $"(期望≈16383；>0 表示读写机制正常)");
            }
            catch (System.Exception e)
            {
                // v16at：自检失败在本游戏是**预期**的 —— 它的音频走 FMOD 中间件，
                //   AudioClip.Create/SetData 的原生路径本身就不完整（抛原生异常）。
                //   这条结论不影响导出：真正的取数通道是 PCMReaderCallback 直调。
                ExplorerCore.Log("[音频自检] 跳过（本游戏 AudioClip.Create/SetData 原生路径不可用，" +
                    "属预期；导出改走 PCMReaderCallback 直调通道）: " + ExMsg(e));
            }
            finally
            {
                // ★ v16aw：自检用的临时 clip **必须销毁**。
                //   实机踩过：`AudioClip.Create("UE_AudioSelfTest", ...)` 在原生层已经建出了对象，
                //   但随后 `SetData/Invoke` 抛原生异常 → 我们拿不到托管引用 → 对象永久泄漏。
                //   它不属于任何 `.resource`，却会混进 `FindObjectsOfTypeAll(AudioClip)` 的序列
                //   （实测排在**最前面**，三元组 (1,44100,44100) 在 bank 里根本不存在），
                //   直接把「整段对位」模型顶掉。这里按**名字**兜底清理，覆盖拿不到引用的情形。
                AudioBankExporter.DestroyClipsNamed("UE_AudioSelfTest");
            }
        }

        /// <summary>读取 AudioClip.loadState / loadType 的文本形式（属性可能不存在，返回 "?"）。</summary>
        private static string GetClipLoadStateText(AudioClip clip)
        {
            try
            {
                System.Reflection.PropertyInfo ls = typeof(AudioClip).GetProperty("loadState");
                if (ls != null) return System.Convert.ToString(ls.GetValue(clip, null));
            }
            catch { }
            return "?";
        }

        /// <summary>
        /// v16ap：确保 AudioClip 数据已加载（流式/内存压缩音频默认未加载，GetData 会失败）。
        /// </summary>
        private static void EnsureClipDataLoaded(AudioClip clip)
        {
            try
            {
                System.Reflection.PropertyInfo lsProp = typeof(AudioClip).GetProperty("loadState");
                if (lsProp == null) return;

                string state = null;
                try { state = System.Convert.ToString(lsProp.GetValue(clip, null)); } catch { }
                if (state == null || state == "Loaded") return;

                System.Reflection.MethodInfo load = typeof(AudioClip).GetMethod(
                    "LoadAudioData", BindingFlags.Public | BindingFlags.Instance);
                if (load != null)
                {
                    try { load.Invoke(clip, null); } catch { }
                }

                // 轮询等待（最长约 2 秒，避免长时间卡主线程）
                for (int i = 0; i < 40; i++)
                {
                    try { state = System.Convert.ToString(lsProp.GetValue(clip, null)); } catch { }
                    if (state == "Loaded" || state == "Failed") break;
                    System.Threading.Thread.Sleep(50);
                }

                string loadType = "?";
                try
                {
                    System.Reflection.PropertyInfo lt = typeof(AudioClip).GetProperty("loadType");
                    if (lt != null) loadType = System.Convert.ToString(lt.GetValue(clip, null));
                }
                catch { }
                ExplorerCore.Log($"[音频] 加载 {clip.name}: loadState={state} loadType={loadType}");
            }
            catch { }
        }

        /// <summary>反射调用返回值转 bool（支持 bool / null）。</summary>
        private static bool IsTrueObj(object o)
        {
            if (o == null) return false;
            if (o is bool b) return b;
            try { return System.Convert.ToBoolean(o); } catch { return false; }
        }

        /// <summary>
        /// 导出材质信息为文本
        /// </summary>
        public static void ExportMaterialInfo(Material mat, string path)
        {
            if (!mat)
            {
                ExplorerCore.LogWarning("材质为空，无法导出！");
                return;
            }

            if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                path += ".txt";

            path = EnsureValidPath(path);

            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("=== Material Info ===");
                sb.AppendLine($"Name: {mat.name}");
                sb.AppendLine($"Shader: {mat.shader.name}");
                sb.AppendLine($"RenderQueue: {mat.renderQueue}");
                sb.AppendLine();

                // 常见颜色属性
                string[] colorProps = new string[]
                {
                    "_Color", "_BaseColor", "_EmissionColor", "_SpecColor",
                    "_TintColor", "_BorderColor", "_RimColor", "_MainColor"
                };
                sb.AppendLine("--- Colors ---");
                foreach (string prop in colorProps)
                {
                    if (mat.HasProperty(prop))
                    {
                        Color c = mat.GetColor(prop);
                        sb.AppendLine($"[Color] {prop} = ({c.r:F4}, {c.g:F4}, {c.b:F4}, {c.a:F4})");
                    }
                }

                // 常见浮点属性
                string[] floatProps = new string[]
                {
                    "_Glossiness", "_Metallic", "_BumpScale", "_Parallax",
                    "_OcclusionStrength", "_EmissionScale", "_Cutoff", "_AlphaCutoff",
                    "_DetailNormalMapScale", "_UVSec", "_Mode", "_ZWrite", "_SrcBlend", "_DstBlend"
                };
                sb.AppendLine();
                sb.AppendLine("--- Floats ---");
                foreach (string prop in floatProps)
                {
                    if (mat.HasProperty(prop))
                    {
                        float f = mat.GetFloat(prop);
                        sb.AppendLine($"[Float] {prop} = {f:F6}");
                    }
                }

                // 常见纹理属性
                string[] texProps = new string[]
                {
                    "_MainTex", "_BumpMap", "_MetallicGlossMap", "_SpecGlossMap",
                    "_EmissionMap", "_DetailMask", "_DetailAlbedoMap", "_DetailNormalMap",
                    "_ParallaxMap", "_OcclusionMap", "_BaseMap", "_BaseColorMap",
                    "_NormalMap", "_MaskMap"
                };
                sb.AppendLine();
                sb.AppendLine("--- Textures ---");
                foreach (string prop in texProps)
                {
                    if (mat.HasProperty(prop))
                    {
                        Texture tex = mat.GetTexture(prop);
                        Vector2 scale = mat.GetTextureScale(prop);
                        Vector2 offset = mat.GetTextureOffset(prop);
                        string texName = tex ? tex.name : "(null)";
                        int w = tex ? tex.width : 0;
                        int h = tex ? tex.height : 0;
                        sb.AppendLine($"[Texture] {prop} = {texName} ({w}x{h}) scale:({scale.x:F2},{scale.y:F2}) offset:({offset.x:F2},{offset.y:F2})");
                    }
                }

                // 关键字
                sb.AppendLine();
                sb.AppendLine("--- Shader Keywords ---");
                foreach (string kw in mat.shaderKeywords)
                    sb.AppendLine($"  {kw}");

                File.WriteAllText(path, sb.ToString());
                ExplorerCore.Log($"材质信息已导出: {path}");
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"导出材质信息失败: {ex.Message}");
            }
        }

#if !CPP
        /// <summary>
        /// 导出动画为 JSON（运行时版本，通过反射尝试获取曲线）
        /// </summary>
        public static void ExportAnimationAsJSON(AnimationClip clip, string path)
        {
            if (!clip)
            {
                ExplorerCore.LogWarning("动画为空，无法导出！");
                return;
            }

            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                path += ".json";

            path = EnsureValidPath(path);

            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"name\": \"{clip.name}\",");
                sb.AppendLine($"  \"length\": {clip.length:F4},");
                sb.AppendLine($"  \"frameRate\": {clip.frameRate:F2},");
                sb.AppendLine($"  \"loopTime\": {clip.isLooping.ToString().ToLower()},");
                sb.AppendLine($"  \"wrapMode\": \"{clip.wrapMode}\",");

                // 运行时尝试用反射获取曲线绑定
                bool gotCurves = false;
                try
                {
                    System.Reflection.MethodInfo getBindings = typeof(AnimationClip).GetMethod("GetCurveBindings",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (getBindings != null)
                    {
                        object bindings = getBindings.Invoke(clip, null);
                        System.Collections.IEnumerable bindingList = bindings as System.Collections.IEnumerable;
                        if (bindingList != null)
                        {
                            sb.AppendLine("  \"curves\": [");
                            bool first = true;
                            foreach (object binding in bindingList)
                            {
                                System.Reflection.PropertyInfo pathProp = binding.GetType().GetProperty("path");
                                System.Reflection.PropertyInfo propNameProp = binding.GetType().GetProperty("propertyName");
                                System.Reflection.PropertyInfo typeProp = binding.GetType().GetProperty("type");

                                string curvePath = pathProp?.GetValue(binding, null)?.ToString() ?? "";
                                string propName = propNameProp?.GetValue(binding, null)?.ToString() ?? "";
                                string typeName = (typeProp?.GetValue(binding, null) as System.Type)?.Name ?? "Unknown";

                                // 获取曲线
                                System.Reflection.MethodInfo getCurve = typeof(AnimationClip).GetMethod("GetEditorCurve",
                                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                AnimationCurve curve = null;
                                if (getCurve != null)
                                    curve = getCurve.Invoke(clip, new object[] { binding }) as AnimationCurve;

                                if (!first)
                                    sb.AppendLine(",");
                                first = false;

                                sb.AppendLine("    {");
                                sb.AppendLine($"      \"path\": \"{curvePath}\",");
                                sb.AppendLine($"      \"propertyName\": \"{propName}\",");
                                sb.AppendLine($"      \"type\": \"{typeName}\",");

                                if (curve != null && curve.keys.Length > 0)
                                {
                                    sb.AppendLine("      \"keys\": [");
                                    for (int i = 0; i < curve.keys.Length; i++)
                                    {
                                        Keyframe key = curve.keys[i];
                                        if (i > 0)
                                            sb.AppendLine(",");
                                        sb.Append($"        {{\"time\":{key.time:F4},\"value\":{key.value:F4},\"inTangent\":{key.inTangent:F4},\"outTangent\":{key.outTangent:F4}}}");
                                    }
                                    sb.AppendLine();
                                    sb.AppendLine("      ]");
                                }
                                else
                                {
                                    sb.AppendLine("      \"keys\": []");
                                }
                                sb.Append("    }");
                            }
                            sb.AppendLine();
                            sb.AppendLine("  ]");
                            gotCurves = true;
                        }
                    }
                }
                catch { }

                if (!gotCurves)
                {
                    sb.AppendLine("  \"curves\": [],");
                    sb.AppendLine("  \"note\": \"运行时无法获取曲线数据，仅导出基本信息。请使用 AssetRipper 等工具获取完整动画数据。\"");
                }

                sb.AppendLine("}");

                File.WriteAllText(path, sb.ToString());
                ExplorerCore.Log($"动画已导出: {path}");
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"导出动画失败: {ex.Message}");
            }
        }
#endif

        /// <summary>
        /// 导出动画基本信息（用于无法直接访问 AnimationClip 的情况）
        /// </summary>
        public static void ExportAnimationInfo(object animObj, string path)
        {
            if (animObj == null)
                return;

            if (!path.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase))
                path += ".json";
            path = EnsureValidPath(path);

            try
            {
                System.Type type = animObj.GetType();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"type\": \"{type.Name}\",");

                // 尝试获取常见属性
                string[] props = { "name", "length", "frameRate", "isLooping", "wrapMode" };
                bool first = true;
                foreach (string propName in props)
                {
                    PropertyInfo prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null)
                    {
                        object val = prop.GetValue(animObj, null);
                        if (!first)
                            sb.AppendLine(",");
                        first = false;
                        if (val is string s)
                            sb.Append($"  \"{propName}\": \"{s}\"");
                        else
                            sb.Append($"  \"{propName}\": {val}");
                    }
                }
                sb.AppendLine();
                sb.AppendLine("}");
                File.WriteAllText(path, sb.ToString());
                ExplorerCore.Log($"动画信息已导出: {path}");
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"导出动画信息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取默认输出目录
        /// </summary>
        public static string GetDefaultOutputDir()
        {
            try
            {
                return ConfigManager.Default_Output_Path.Value;
            }
            catch
            {
                return Path.Combine(Path.Combine(Application.dataPath, ".."), "UnityExplorer_Exports");
            }
        }

        /// <summary>
        /// 确保路径有效（创建目录等）
        /// </summary>
        static string EnsureValidPath(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 清理文件名中的非法字符
            string fileName = Path.GetFileName(path);
            foreach (char c in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(c, '_');

            return Path.Combine(dir, fileName);
        }

        #endregion
    }
}
