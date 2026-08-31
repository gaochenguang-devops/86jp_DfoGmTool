using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using DfoGmTool.ImagePack;
using DfoGmTool.ServerCore.Game.Inventory;
using DfoGmTool.ServerCore.GameWorld;
using DfoGmTool.ServerCore.Infrastructure;
using DfoGmTool.Services;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.SelfTests
{
    // 从上游 S4A21GmTool 迁过来的六项功能的自测, 分三层跑, 前面的层不依赖外部数据:
    //   1) 纯算法层: PNG 编码 / NPK 表项解密 / IMG 帧解码 / 副职业等级曲线 / 本机文件框守卫;
    //   2) 临时库 + 占位 PVF: 邮箱列表与单删/清空的物理删除语义(不需要解析 PVF);
    //   3) 临时库 + 真实 PVF: 整套发放、副职业落库、物品预览/图标。
    // 第三层缺 PVF 时按 FAIL 记一笔并跳过, 与其它 PVF 自测的口径一致, 但前两层结果照常输出。
    internal static class UpstreamPortedFeatureSelfTest
    {
        private const int MailAccountOne = 820001;
        private const int MailAccountTwo = 820002;
        private const int MailCharacterOne = 820011;
        private const int MailCharacterTwo = 820012;
        private const int PvfAccount = 820101;
        private const int SetCharacter = 820111;
        private const int GbkNameCharacter = 820112;
        private const int ExpertCharacter = 820113;

        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        private static int _failures;

        internal static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== UPSTREAM_PORTED_FEATURES selftest ===");
            try
            {
                CheckPngEncoder();
                CheckNpkNameCipher();
                CheckNpkArchive();
                CheckImageDecoder();
                CheckImagePackLibrary();
                CheckExpertJobCurve();
                CheckNativePathDialogGuard();
                RunMailboxTier();
                RunPvfTier();
                Console.WriteLine(_failures == 0
                    ? "UpstreamPortedFeatureSelfTest OK"
                    : "UpstreamPortedFeatureSelfTest FAIL: " + _failures);
                return _failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("UpstreamPortedFeatureSelfTest EXCEPTION: " + ex);
                return 1;
            }
        }
        // ---------------- 第 1 层: PNG 编码 ----------------

        private static void CheckPngEncoder()
        {
            var rgba = new byte[]
            {
                1, 2, 3, 4, 5, 6, 7, 8,
                9, 10, 11, 12, 13, 14, 15, 16,
            };
            var png = PngEncoder.EncodeRgba(2, 2, rgba);
            Check("png keeps the 8-byte signature",
                png.Length > 8 && png.Take(8).SequenceEqual(PngSignature));
            Check("png parses back as IHDR(8bit/RGBA) + IDAT + IEND with valid CRC32",
                TryParsePng(png, out var width, out var height, out var decoded, out var pngError), pngError);
            Check("png reports the encoded canvas size", width == 2 && height == 2);
            Check("png IDAT inflates back to the source RGBA with filter 0",
                decoded != null && decoded.SequenceEqual(rgba));
            Check("png accepts an RGBA buffer longer than width*height*4",
                TryParsePng(PngEncoder.EncodeRgba(1, 1, new byte[64]), out var wideWidth, out _, out _, out _)
                && wideWidth == 1);
            Check("png rejects a short RGBA buffer",
                ThrowsArgumentException(() => PngEncoder.EncodeRgba(2, 2, new byte[15])));
            Check("png rejects a null RGBA buffer",
                ThrowsArgumentException(() => PngEncoder.EncodeRgba(1, 1, null)));
            Check("png rejects a non-positive canvas",
                ThrowsArgumentException(() => PngEncoder.EncodeRgba(0, 1, new byte[4]))
                && ThrowsArgumentException(() => PngEncoder.EncodeRgba(1, -1, new byte[4])));
        }

        // ---------------- 第 1 层: NPK 表项名解密 ----------------

        private static void CheckNpkNameCipher()
        {
            Check("npk name round-trips through the XOR key",
                NpkNameCipher.Decrypt(EncryptName("sprite/item/equipment/weapon/alpha.img"))
                    == "sprite/item/equipment/weapon/alpha.img");
            Check("npk name converts backslashes to forward slashes",
                NpkNameCipher.Decrypt(EncryptName("sprite\\item\\beta.img")) == "sprite/item/beta.img");
            Check("npk name stops right after .img",
                NpkNameCipher.Decrypt(EncryptName("sprite/item/gamma.imgTRAILING")) == "sprite/item/gamma.img");
            Check("npk name without .img keeps only path-safe characters",
                NpkNameCipher.Decrypt(EncryptName("sprite/item/delta*junk")) == "sprite/item/delta");
            Check("npk name decrypt tolerates null/empty input",
                NpkNameCipher.Decrypt(null) == string.Empty
                && NpkNameCipher.Decrypt(Array.Empty<byte>()) == string.Empty);
            Check("img path normalization lowercases and keeps the sprite root",
                NpkNameCipher.NormalizeImgPath("Sprite/Item/A.IMG") == "sprite/item/a.img");
            Check("img path normalization adds the missing sprite prefix",
                NpkNameCipher.NormalizeImgPath("item/a.img") == "sprite/item/a.img"
                && NpkNameCipher.NormalizeImgPath("/item/a.img") == "sprite/item/a.img"
                && NpkNameCipher.NormalizeImgPath("./item/a.img") == "sprite/item/a.img");
            Check("img path normalization strips PVF backtick quoting",
                NpkNameCipher.NormalizeImgPath("`sprite/item/a.img`") == "sprite/item/a.img");
            Check("img path normalization rejects blank input",
                NpkNameCipher.NormalizeImgPath(null) == null
                && NpkNameCipher.NormalizeImgPath("   ") == null);
        }
        // ---------------- 第 1 层: NPK 归档表 ----------------

        private static void CheckNpkArchive()
        {
            var directory = CreateTempDirectory("dfogm-upstream-npk-");
            try
            {
                var alpha = Encoding.ASCII.GetBytes("alpha-entry-payload");
                var noExt = Encoding.ASCII.GetBytes("no-extension-payload");
                var sanitized = Encoding.ASCII.GetBytes("sanitized-payload");
                var duplicate = Encoding.ASCII.GetBytes("duplicate-must-lose");
                var goodBytes = BuildNpk(
                    ("sprite/item/equipment/weapon/alpha.img", alpha),
                    ("sprite/item/noext", noExt),
                    ("sprite/item/delta*junk", sanitized),
                    ("sprite/item/equipment/weapon/alpha.img", duplicate));
                var good = Path.Combine(directory, "good.NPK");
                File.WriteAllBytes(good, goodBytes);

                Check("npk opens a NeoplePack_Bill archive", NpkArchive.TryOpen(good, out var archive));
                var names = archive == null
                    ? new List<string>()
                    : archive.EntryNames.ToList();
                Check("npk drops the duplicate table row instead of overwriting", names.Count == 3);
                Check("npk indexes normalized entry names",
                    names.Contains("sprite/item/equipment/weapon/alpha.img")
                    && names.Contains("sprite/item/noext")
                    && names.Contains("sprite/item/delta"));
                Check("npk reads the first row that claimed a name",
                    archive != null
                    && archive.TryRead("sprite/item/equipment/weapon/alpha.img", out var alphaBlob)
                    && alphaBlob.SequenceEqual(alpha));
                Check("npk resolves a query that omits sprite/ and .img",
                    archive != null
                    && archive.TryRead("item/equipment/weapon/alpha", out var loose)
                    && loose.SequenceEqual(alpha));
                Check("npk resolves an .img query against an extension-less entry",
                    archive != null
                    && archive.TryRead("sprite/item/noext.img", out var stripped)
                    && stripped.SequenceEqual(noExt));
                Check("npk refuses an unknown entry",
                    archive != null && !archive.TryRead("sprite/item/missing.img", out _));

                CheckNpkRejections(directory, goodBytes);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        private static void CheckNpkRejections(string directory, byte[] goodBytes)
        {
            Check("npk rejects a blank or missing path",
                !NpkArchive.TryOpen(null, out _)
                && !NpkArchive.TryOpen("   ", out _)
                && !NpkArchive.TryOpen(Path.Combine(directory, "nope.NPK"), out _));

            var badMagic = (byte[])goodBytes.Clone();
            badMagic[3] = (byte)'X';
            Check("npk rejects a wrong magic", !TryOpenBytes(directory, "bad-magic.NPK", badMagic));

            var zeroCount = (byte[])goodBytes.Clone();
            WriteInt32(zeroCount, 16, 0);
            Check("npk rejects a zero entry count", !TryOpenBytes(directory, "zero-count.NPK", zeroCount));

            var hugeCount = (byte[])goodBytes.Clone();
            WriteInt32(hugeCount, 16, 20001);
            Check("npk rejects an entry count above the 20000 cap",
                !TryOpenBytes(directory, "huge-count.NPK", hugeCount));

            var truncated = goodBytes.Take(120).ToArray();
            Check("npk rejects a truncated table", !TryOpenBytes(directory, "truncated.NPK", truncated));

            var allPastEof = (byte[])goodBytes.Clone();
            WriteInt32(allPastEof, 16, 1);
            WriteInt32(allPastEof, 20 + 4, int.MaxValue);
            Check("npk rejects an archive whose only row points past EOF",
                !TryOpenBytes(directory, "past-eof.NPK", allPastEof));

            var oneRowPastEof = (byte[])goodBytes.Clone();
            WriteInt32(oneRowPastEof, 16, 2);
            WriteInt32(oneRowPastEof, 20 + 264 + 4, int.MaxValue);
            Check("npk skips a single out-of-range row and keeps the rest",
                TryOpenBytes(directory, "one-past-eof.NPK", oneRowPastEof, out var partial)
                && partial.EntryNames.Count() == 1);
        }
        // ---------------- 第 1 层: IMG v2 帧解码 ----------------

        private static void CheckImageDecoder()
        {
            var argb8888 = BuildImg(new[] { Argb8888Frame(2, 1, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }) });
            Check("img decodes ARGB8888 and swaps BGRA into RGBA",
                NpkImageDecoder.TryDecodeFrame(argb8888, 0, out var width, out var height, out var rgba)
                && width == 2 && height == 1
                && rgba.SequenceEqual(new byte[] { 3, 2, 1, 4, 7, 6, 5, 8 }));

            // 0xFC1F = alpha 位 + R31 + G0 + B31; 0x4000 = 无 alpha 位 + R16
            var argb1555 = BuildImg(new[] { PixelFrame(0x0E, 2, 1, new byte[] { 0x1F, 0xFC, 0x00, 0x40 }) });
            Check("img decodes ARGB1555 with 5-bit scaling and the alpha bit",
                NpkImageDecoder.TryDecodeFrame(argb1555, 0, out _, out _, out var rgba1555)
                && rgba1555.SequenceEqual(new byte[] { 255, 0, 255, 255, 131, 0, 0, 0 }));

            // 0xF0A5 = A15 R0 G10 B5, 每个 nibble ×17
            var argb4444 = BuildImg(new[] { PixelFrame(0x0F, 1, 1, new byte[] { 0xA5, 0xF0 }) });
            Check("img decodes ARGB4444 by scaling each nibble by 17",
                NpkImageDecoder.TryDecodeFrame(argb4444, 0, out _, out _, out var rgba4444)
                && rgba4444.SequenceEqual(new byte[] { 0, 170, 85, 255 }));

            var raw = new byte[] { 9, 8, 7, 255 };
            var deflated = BuildImg(new[] { PixelFrame(0x10, 1, 1, Deflate(raw), compressed: 1) });
            Check("img inflates a zlib-compressed frame",
                NpkImageDecoder.TryDecodeFrame(deflated, 0, out _, out _, out var inflated)
                && inflated.SequenceEqual(new byte[] { 7, 8, 9, 255 }));
            var lying = BuildImg(new[] { PixelFrame(0x10, 1, 1, raw, compressed: 1) });
            Check("img falls back to raw bytes when the compressed flag lies",
                NpkImageDecoder.TryDecodeFrame(lying, 0, out _, out _, out var fallback)
                && fallback.SequenceEqual(new byte[] { 7, 8, 9, 255 }));

            var linked = BuildImg(new[]
            {
                Argb8888Frame(1, 1, new byte[] { 1, 2, 3, 255 }),
                LinkFrame(0),
            });
            Check("img follows a link frame to its target",
                NpkImageDecoder.TryDecodeFrame(linked, 1, out var linkWidth, out _, out var linkRgba)
                && linkWidth == 1
                && linkRgba.SequenceEqual(new byte[] { 3, 2, 1, 255 }));
            var cycle = BuildImg(new[] { LinkFrame(1), LinkFrame(0) });
            Check("img refuses a link cycle instead of recursing forever",
                !NpkImageDecoder.TryDecodeFrame(cycle, 0, out _, out _, out _));
            Check("img refuses a link pointing outside the frame table",
                !NpkImageDecoder.TryDecodeFrame(BuildImg(new[] { LinkFrame(7) }), 0, out _, out _, out _));

            CheckImageDecoderCanvas();
            CheckImageDecoderRejections();
        }

        private static void CheckImageDecoderCanvas()
        {
            var offset = Argb8888Frame(2, 1, new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 });
            offset.KeyX = 1;
            offset.KeyY = 2;
            offset.MaxWidth = 4;
            offset.MaxHeight = 4;
            var blitted = BuildImg(new[] { offset });
            var decoded = NpkImageDecoder.TryDecodeFrame(blitted, 0, out var width, out var height, out var rgba);
            Check("img blits a keyed frame onto its max canvas", decoded && width == 4 && height == 4);
            Check("img places the keyed frame at keyX/keyY and leaves the rest transparent",
                decoded
                && PixelAt(rgba, 4, 1, 2).SequenceEqual(new byte[] { 255, 0, 0, 255 })
                && PixelAt(rgba, 4, 2, 2).SequenceEqual(new byte[] { 255, 0, 0, 255 })
                && PixelAt(rgba, 4, 0, 0).SequenceEqual(new byte[] { 0, 0, 0, 0 })
                && PixelAt(rgba, 4, 3, 3).SequenceEqual(new byte[] { 0, 0, 0, 0 }));

            var oversize = Argb8888Frame(1, 1, new byte[] { 1, 2, 3, 255 });
            oversize.MaxWidth = 512;
            oversize.MaxHeight = 512;
            Check("img falls back to the frame size when the canvas exceeds 256",
                NpkImageDecoder.TryDecodeFrame(BuildImg(new[] { oversize }), 0, out var smallWidth, out var smallHeight, out _)
                && smallWidth == 1 && smallHeight == 1);
        }
        private static void CheckImageDecoderRejections()
        {
            var valid = BuildImg(new[] { Argb8888Frame(1, 1, new byte[] { 1, 2, 3, 255 }) });
            Check("img refuses null/short blobs",
                !NpkImageDecoder.TryDecodeFrame(null, 0, out _, out _, out _)
                && !NpkImageDecoder.TryDecodeFrame(new byte[31], 0, out _, out _, out _));
            Check("img refuses a wrong magic",
                !NpkImageDecoder.TryDecodeFrame(
                    BuildImg(new[] { Argb8888Frame(1, 1, new byte[] { 1, 2, 3, 255 }) }, magic: "Neople Bmp File"),
                    0, out _, out _, out _));
            Check("img refuses a version other than 2",
                !NpkImageDecoder.TryDecodeFrame(
                    BuildImg(new[] { Argb8888Frame(1, 1, new byte[] { 1, 2, 3, 255 }) }, version: 3),
                    0, out _, out _, out _));
            Check("img refuses a frame index outside the frame count",
                !NpkImageDecoder.TryDecodeFrame(valid, 1, out _, out _, out _)
                && !NpkImageDecoder.TryDecodeFrame(valid, -1, out _, out _, out _));
            Check("img refuses an unknown pixel format",
                !NpkImageDecoder.TryDecodeFrame(
                    BuildImg(new[] { PixelFrame(0x0D, 1, 1, new byte[] { 1, 2, 3, 4 }) }),
                    0, out _, out _, out _));
            Check("img refuses a payload shorter than width*height",
                !NpkImageDecoder.TryDecodeFrame(
                    BuildImg(new[] { PixelFrame(0x10, 4, 4, new byte[] { 1, 2, 3, 4 }) }),
                    0, out _, out _, out _));
            var pastEnd = (byte[])valid.Clone();
            WriteInt32(pastEnd, 32 + 16, int.MaxValue);
            Check("img refuses a frame whose pixel size runs past the blob",
                !NpkImageDecoder.TryDecodeFrame(pastEnd, 0, out _, out _, out _));
        }

        // ---------------- 第 1 层: ImagePacks2 目录与端到端渲染 ----------------

        private static void CheckImagePackLibrary()
        {
            var outer = CreateTempDirectory("dfogm-upstream-imagepacks-");
            try
            {
                var nested = Path.Combine(outer, "ImagePacks2");
                Directory.CreateDirectory(nested);
                var icon = BuildImg(new[]
                {
                    Argb8888Frame(2, 2, new byte[]
                    {
                        10, 20, 30, 255, 11, 21, 31, 255,
                        12, 22, 32, 255, 13, 23, 33, 255,
                    }),
                    Argb8888Frame(1, 1, new byte[] { 0, 255, 0, 255 }),
                });
                var mark = BuildImg(new[]
                {
                    Argb8888Frame(2, 2, new byte[]
                    {
                        255, 0, 0, 255, 0, 0, 0, 0,
                        0, 0, 0, 0, 0, 0, 0, 0,
                    }),
                });
                File.WriteAllBytes(Path.Combine(nested, "sprite_item.NPK"), BuildNpk(
                    ("sprite/item/equipment/weapon/selftest.img", icon),
                    ("sprite/item/equipment/weapon/selftest_mark.img", mark),
                    ("sprite/interface/upstream_probe.img", icon)));

                var empty = CreateTempDirectory("dfogm-upstream-empty-");
                try
                {
                    Check("imagepacks root rejects blank/missing/unknown directories",
                        !ImagePackLibrary.TryResolveRoot(null, out _)
                        && !ImagePackLibrary.TryResolveRoot("  ", out _)
                        && !ImagePackLibrary.TryResolveRoot(Path.Combine(outer, "nope"), out _)
                        && !ImagePackLibrary.TryResolveRoot(empty, out _));
                }
                finally
                {
                    DeleteDirectory(empty);
                }

                Check("imagepacks root promotes a nested ImagePacks2 directory",
                    ImagePackLibrary.TryResolveRoot(outer, out var promoted)
                    && string.Equals(promoted, nested, StringComparison.OrdinalIgnoreCase));
                Check("imagepacks root accepts the pack directory itself",
                    ImagePackLibrary.TryResolveRoot(nested, out var direct)
                    && string.Equals(direct, nested, StringComparison.OrdinalIgnoreCase));

                var library = ImagePackLibrary.TryOpen(outer);
                Check("imagepacks library opens against the promoted root",
                    library != null && string.Equals(library.Root, nested, StringComparison.OrdinalIgnoreCase));
                if (library == null)
                    return;

                CheckImagePackRendering(library);
            }
            finally
            {
                DeleteDirectory(outer);
            }
        }
        private static void CheckImagePackRendering(ImagePackLibrary library)
        {
            const string iconPath = "sprite/item/equipment/weapon/selftest.img";
            const string markPath = "sprite/item/equipment/weapon/selftest_mark.img";

            Check("imagepacks renders frame 0 as a PNG of the decoded size",
                library.TryRenderPng(iconPath, 0, null, 0, out var iconPng)
                && TryParsePng(iconPng, out var iconWidth, out var iconHeight, out var iconRgba, out _)
                && iconWidth == 2 && iconHeight == 2
                && iconRgba.SequenceEqual(new byte[]
                {
                    30, 20, 10, 255, 31, 21, 11, 255,
                    32, 22, 12, 255, 33, 23, 13, 255,
                }));
            Check("imagepacks renders a second frame of the same img",
                library.TryRenderPng(iconPath, 1, null, 0, out var framePng)
                && TryParsePng(framePng, out var frameWidth, out var frameHeight, out var frameRgba, out _)
                && frameWidth == 1 && frameHeight == 1
                && frameRgba.SequenceEqual(new byte[] { 0, 255, 0, 255 }));
            Check("imagepacks blits the mark img over the base frame",
                library.TryRenderPng(iconPath, 0, markPath, 0, out var markedPng)
                && TryParsePng(markedPng, out _, out _, out var markedRgba, out _)
                && markedRgba.SequenceEqual(new byte[]
                {
                    0, 0, 255, 255, 31, 21, 11, 255,
                    32, 22, 12, 255, 33, 23, 13, 255,
                }));
            Check("imagepacks caches the encoded PNG per img/frame/mark key",
                library.TryRenderPng(iconPath, 0, null, 0, out var cached)
                && ReferenceEquals(cached, iconPng));
            Check("imagepacks resolves case/prefix/backtick variants to the same PNG",
                library.TryRenderPng("Item/Equipment/Weapon/SELFTEST.IMG", 0, null, 0, out var loose)
                && loose.SequenceEqual(iconPng)
                && library.TryRenderPng("`" + iconPath + "`", 0, null, 0, out var quoted)
                && quoted.SequenceEqual(iconPng));
            Check("imagepacks falls back to the sprite_item* entry index for a foreign path",
                library.TryRenderPng("sprite/interface/upstream_probe.img", 0, null, 0, out var probePng)
                && probePng.SequenceEqual(iconPng));
            Check("imagepacks refuses a missing img, a missing frame and a blank path",
                !library.TryRenderPng("sprite/item/equipment/weapon/nope.img", 0, null, 0, out _)
                && !library.TryRenderPng(iconPath, 5, null, 0, out _)
                && !library.TryRenderPng("   ", 0, null, 0, out _));
            Check("imagepacks ignores a missing mark instead of failing the render",
                library.TryRenderPng(iconPath, 0, "sprite/item/equipment/weapon/nomark.img", 0, out var noMark)
                && noMark.SequenceEqual(iconPng));
            Check("imagepacks reports no window chrome when windowcommon.img is absent",
                !library.TryRenderWindowChrome(out _));
        }
        // ---------------- 第 1 层: 副职业等级曲线 ----------------

        private static void CheckExpertJobCurve()
        {
            var definition = new ExpertJobDefinition
            {
                Type = 1,
                Name = "自测副职业",
                ExperienceThresholds = new[] { 100, 300, 600 },
                AutoLearnRecipes = new Dictionary<int, int> { { 1, 11 }, { 2, 22 }, { 3, 33 } },
                InitialMachineGrade = 1,
                InitialEndurance = 7,
                RepairEnduranceCaps = new[] { 50, 80, 120 },
            };

            Check("expert job max level/exp come from the .exj thresholds",
                definition.MaxLevel == 3 && definition.MaxExp == 600);
            Check("expert job level advances only once a threshold is reached",
                definition.GetLevel(0) == 1 && definition.GetLevel(99) == 1
                && definition.GetLevel(100) == 2 && definition.GetLevel(299) == 2
                && definition.GetLevel(300) == 3);
            Check("expert job level clamps at max instead of overflowing",
                definition.GetLevel(600) == 3 && definition.GetLevel(uint.MaxValue) == 3);
            Check("expert job exp-for-level round-trips back to the same level",
                definition.GetExpForLevel(1) == 0
                && definition.GetExpForLevel(2) == 100
                && definition.GetExpForLevel(3) == 600
                && definition.GetExpForLevel(99) == 600
                && definition.GetLevel(definition.GetExpForLevel(2)) == 2
                && definition.GetLevel(definition.GetExpForLevel(3)) == 3);
            Check("expert job auto-learn recipes accumulate by level",
                definition.GetAutoLearnRecipeIds(0).SequenceEqual(new[] { 11 })
                && definition.GetAutoLearnRecipeIds(100).SequenceEqual(new[] { 11, 22 })
                && definition.GetAutoLearnRecipeIds(600).SequenceEqual(new[] { 11, 22, 33 }));
            Check("expert job machine grade cap follows the repair table length",
                definition.MaxMachineGrade == 3);
            Check("expert job endurance cap maps grade to the repair table",
                definition.GetEnduranceCap(1) == 50
                && definition.GetEnduranceCap(2) == 80
                && definition.GetEnduranceCap(3) == 120);
            Check("expert job endurance cap falls back to the initial value out of range",
                definition.GetEnduranceCap(0) == 7 && definition.GetEnduranceCap(4) == 7);

            var degenerate = new ExpertJobDefinition { Type = 9, InitialMachineGrade = 2, InitialEndurance = 5 };
            Check("expert job without thresholds stays at level 1 with zero exp",
                degenerate.MaxLevel == 1 && degenerate.MaxExp == 0
                && degenerate.GetLevel(999) == 1 && degenerate.GetExpForLevel(5) == 0
                && degenerate.GetAutoLearnRecipeIds(999).Count == 0
                && degenerate.MaxMachineGrade == 2 && degenerate.GetEnduranceCap(1) == 5);
        }
        // ---------------- 第 1 层: 本机选择框守卫 ----------------

        // 只探未知/空的 Kind: 这条分支在起 STA 线程之前就返回, 不会真的弹出 COM 窗口。
        private static void CheckNativePathDialogGuard()
        {
            var expected = OperatingSystem.IsWindows() ? "未知的选择类型。" : "当前系统请直接填写路径。";
            var nullRequest = NativePathDialog.Pick(null);
            var blankKind = NativePathDialog.Pick(new BrowsePathRequest { Kind = "   " });
            var unknownKind = NativePathDialog.Pick(new BrowsePathRequest { Kind = "explorer", CurrentPath = "C:\\" });
            Check("native dialog refuses a null request before opening a window",
                !IsSuccess(nullRequest) && ErrorOf(nullRequest) == expected);
            Check("native dialog refuses a blank kind",
                !IsSuccess(blankKind) && ErrorOf(blankKind) == expected);
            Check("native dialog refuses an unknown kind",
                !IsSuccess(unknownKind) && ErrorOf(unknownKind) == expected);
            Check("native dialog never reports a path on the refusal path",
                GetStringProperty(unknownKind, "path") == null);
        }

        // ---------------- 第 2 层: 邮箱列表与物理删除(占位 PVF) ----------------

        private static void RunMailboxTier()
        {
            var dbPath = TempPath("dfogm-upstream-mail-", ".db");
            var placeholderPvf = TempPath("dfogm-upstream-placeholder-", ".pvf");
            try
            {
                var schema = Path.Combine(AppContext.BaseDirectory, "ServerCore", "Sqlite", "item_schema.sql");
                File.WriteAllBytes(placeholderPvf, new byte[] { 0 });
                SqliteDatabaseBootstrap.CreateTestDatabase(dbPath, schema);
                SeedMailbox(dbPath);
                Check("mailbox tier loads a GM config from the temp database",
                    GmConfig.TryCreate(dbPath, placeholderPvf, out var config, out var configError), configError);
                if (config == null)
                    return;

                var gm = new GmService(config, null);
                CheckMailboxList(gm, dbPath);
                CheckMailboxDelete(gm, dbPath);
                CheckMailboxClear(gm, dbPath);
            }
            finally
            {
                DeleteFile(dbPath);
                DeleteFile(placeholderPvf);
            }
        }
        private static void SeedMailbox(string dbPath)
        {
            using var connection = Open(dbPath);
            using var transaction = connection.BeginTransaction();
            Exec(connection, transaction, $@"
INSERT INTO accounts(account_id,m_id,password_hash) VALUES
({MailAccountOne},'upstream-mail-one',''),
({MailAccountTwo},'upstream-mail-two','');");
            // 角色名按 GBK(936) BLOB 存, 与服务端一致。
            Exec(connection, transaction, $@"
INSERT INTO characters(character_id,account_id,name,job,grow_type,level,exp,slot_index) VALUES
({MailCharacterOne},{MailAccountOne},CAST(X'BDC7C9ABD2BB' AS BLOB),0,0,1,0,0),
({MailCharacterTwo},{MailAccountTwo},'upstream-mail-two',0,0,1,0,0);");
            Exec(connection, transaction, $@"
INSERT INTO mailbox_messages(
    message_id,sender_character_id,sender_name,receiver_character_id,receiver_account_id,
    title,body,gold,mail_type,idempotency_key,request_hash,created_at,unlimited_flag,expire_at) VALUES
(9101,0,'DNFadmin',{MailCharacterOne},{MailAccountOne},'私信邮件','正文一',100,1,'gm:upstream-mail-1','hash-one','2026-01-01 10:00:00',0,'2030-01-01 00:00:00'),
(9102,0,'DNFadmin',{MailCharacterOne},{MailAccountOne},'共享邮件','正文二',0,1,NULL,'','2026-01-01 09:00:00',0,'2030-01-01 00:00:00'),
(9103,0,'DNFadmin',{MailCharacterOne},{MailAccountOne},'其它文件夹','正文三',0,1,NULL,'','2026-01-01 06:00:00',0,'2030-01-01 00:00:00'),
(9104,0,'DNFadmin',{MailCharacterOne},{MailAccountOne},'软删除','正文四',0,1,NULL,'','2026-01-01 05:00:00',0,'2030-01-01 00:00:00'),
(9105,0,'DNFadmin',{MailCharacterOne},{MailAccountOne},'保管邮件','正文五',0,1,NULL,'','2026-01-01 08:00:00',1,'9999-12-31 00:00:00'),
(9106,0,'DNFadmin',{MailCharacterOne},{MailAccountOne},'过期邮件','正文六',0,1,NULL,'','2026-01-01 07:00:00',0,'2020-01-01 00:00:00');");
            Exec(connection, transaction, $@"
INSERT INTO mailbox_recipients(message_id,character_id,folder,read_flag,saved_flag,deleted_flag) VALUES
(9101,{MailCharacterOne},0,0,0,0),
(9102,{MailCharacterOne},0,1,0,0),
(9102,{MailCharacterTwo},0,0,0,0),
(9103,{MailCharacterOne},1,0,0,0),
(9104,{MailCharacterOne},0,0,0,1),
(9105,{MailCharacterOne},0,0,1,0),
(9106,{MailCharacterOne},0,0,0,0);");
            // 9101 先插 ordinal=1 再插 ordinal=0, 用来证明列表按 ordinal 而非插入顺序排。
            Exec(connection, transaction, @"
INSERT INTO mailbox_attachments(message_id,ordinal,item_template_id,item_kind,item_count,item_core) VALUES
(9101,1,2000,'material',5,zeroblob(99)),
(9101,0,1000,'consumable',3,zeroblob(99)),
(9102,0,3000,'material',1,NULL),
(9105,0,4000,'material',2,zeroblob(99));");
            Exec(connection, transaction, $@"
INSERT INTO mailbox_system_mail_audit(
    audit_id,message_id,actor_name,audit_reason,receiver_account_id,receiver_character_id,
    gold,attachment_count,mail_type,idempotency_key,request_hash,unlimited_flag,expire_at) VALUES
(7101,9101,'GM','upstream selftest',{MailAccountOne},{MailCharacterOne},100,2,1,'gm:upstream-mail-1','hash-one',0,'2030-01-01 00:00:00');");
            Exec(connection, transaction, @"
INSERT INTO mailbox_system_mail_audit_attachments(audit_id,ordinal,item_template_id,item_kind,item_count) VALUES
(7101,0,1000,'consumable',3),
(7101,1,2000,'material',5);");
            Exec(connection, transaction, $@"
INSERT INTO mailbox_campaigns(campaign_id,payload_hash,status,last_character_id,max_character_id)
VALUES('upstream-selftest-campaign','hash-campaign',0,{MailCharacterOne},{MailCharacterTwo});
INSERT INTO mailbox_campaign_deliveries(campaign_id,character_id,message_id)
VALUES('upstream-selftest-campaign',{MailCharacterOne},9101);");
            transaction.Commit();
        }
        private static void CheckMailboxList(GmService gm, string dbPath)
        {
            var listed = gm.ListMailbox(MailCharacterOne, null);
            Check("mailbox list succeeds without a PVF index", IsSuccess(listed));
            var mails = MailsOf(listed);
            Check("mailbox list skips other folders and soft-deleted rows",
                GetIntProperty(listed, "count") == 4 && mails.Count == 4);
            Check("mailbox list orders newest first",
                mails.Count == 4
                && GetLongProperty(mails[0], "messageId") == 9101
                && GetLongProperty(mails[1], "messageId") == 9102
                && GetLongProperty(mails[2], "messageId") == 9105
                && GetLongProperty(mails[3], "messageId") == 9106);
            if (mails.Count != 4)
                return;

            Check("mailbox list reports the system sender, title, gold and read flag",
                GetStringProperty(mails[0], "senderName") == "DNFadmin"
                && GetStringProperty(mails[0], "title") == "私信邮件"
                && GetStringProperty(mails[0], "body") == "正文一"
                && GetIntProperty(mails[0], "gold") == 100
                && !GetBoolProperty(mails[0], "read")
                && GetBoolProperty(mails[1], "read"));
            Check("mailbox list labels the inbox and the saved folder",
                GetStringProperty(mails[0], "folder") == "收件箱"
                && GetStringProperty(mails[2], "folder") == "保管"
                && GetBoolProperty(mails[2], "saved"));
            Check("mailbox list marks unlimited mail as never expiring",
                GetBoolProperty(mails[2], "unlimited")
                && !GetBoolProperty(mails[2], "expired")
                && GetIntProperty(mails[2], "remainSeconds") == 0);
            Check("mailbox list keeps expired mail visible and flagged",
                GetBoolProperty(mails[3], "expired")
                && GetIntProperty(mails[3], "remainSeconds") == 0
                && !GetBoolProperty(mails[0], "expired")
                && GetIntProperty(mails[0], "remainSeconds") > 0);

            var attachments = AttachmentsOf(mails[0]);
            Check("mailbox list returns attachments ordered by ordinal",
                attachments.Count == 2
                && GetIntProperty(attachments[0], "itemId") == 1000
                && GetIntProperty(attachments[0], "count") == 3
                && GetIntProperty(attachments[1], "itemId") == 2000
                && GetIntProperty(attachments[1], "count") == 5);
            Check("mailbox list leaves names blank and rarity zero without an index",
                attachments.Count == 2
                && GetStringProperty(attachments[0], "name") == string.Empty
                && GetIntProperty(attachments[0], "rarity") == 0
                && !GetBoolProperty(attachments[0], "claimed"));
            Check("mailbox list rejects an invalid or unknown character",
                ErrorOf(gm.ListMailbox(0, null)) == "角色编号无效"
                && ErrorOf(gm.ListMailbox(999999, null)) == "角色不存在: 999999");
            Check("mailbox list never mutates the database",
                LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_messages;") == 6
                && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_recipients;") == 7);
        }
        private static void CheckMailboxDelete(GmService gm, string dbPath)
        {
            var deleted = gm.DeleteMailboxMessage(MailCharacterOne, 9101);
            Check("mailbox delete counts the recipient, message, attachments, audit and campaign refs",
                IsSuccess(deleted)
                && GetIntProperty(deleted, "recipientCount") == 1
                && GetIntProperty(deleted, "messageCount") == 1
                && GetIntProperty(deleted, "attachmentCount") == 2
                && GetIntProperty(deleted, "auditCount") == 1
                && GetIntProperty(deleted, "campaignReferenceCount") == 1);
            Check("mailbox delete physically removes the message, recipients and attachments",
                LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_messages WHERE message_id=9101;") == 0
                && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_recipients WHERE message_id=9101;") == 0
                && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_attachments WHERE message_id=9101;") == 0);
            Check("mailbox delete removes the system-mail audit and its attachments",
                LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_system_mail_audit WHERE message_id=9101;") == 0
                && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_system_mail_audit_attachments WHERE audit_id=7101;") == 0);
            Check("mailbox delete keeps the campaign delivery auditable with a NULL message id",
                LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_campaign_deliveries WHERE character_id=" + MailCharacterOne + ";") == 1
                && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_campaign_deliveries WHERE message_id IS NULL;") == 1);
            Check("mailbox delete is not replayable on the same message",
                ErrorOf(gm.DeleteMailboxMessage(MailCharacterOne, 9101)) == "邮件不存在或不属于该角色: 9101");

            var shared = gm.DeleteMailboxMessage(MailCharacterOne, 9102);
            Check("shared mail delete only drops this character's recipient row",
                IsSuccess(shared)
                && GetIntProperty(shared, "recipientCount") == 1
                && GetIntProperty(shared, "messageCount") == 0
                && GetIntProperty(shared, "attachmentCount") == 0);
            Check("shared mail keeps the root message for the remaining recipient",
                LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_messages WHERE message_id=9102;") == 1
                && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_recipients WHERE message_id=9102;") == 1
                && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_attachments WHERE message_id=9102;") == 1);

            Check("mailbox delete rejects invalid character and message ids",
                ErrorOf(gm.DeleteMailboxMessage(0, 9105)) == "角色编号无效"
                && ErrorOf(gm.DeleteMailboxMessage(MailCharacterOne, 0)) == "邮件编号无效"
                && ErrorOf(gm.DeleteMailboxMessage(MailCharacterOne, -5)) == "邮件编号无效"
                && ErrorOf(gm.DeleteMailboxMessage(999999, 9105)) == "角色不存在或已删除: 999999");
            Check("mailbox delete refuses another character's mail",
                ErrorOf(gm.DeleteMailboxMessage(MailCharacterTwo, 9105)) == "邮件不存在或不属于该角色: 9105"
                && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_messages WHERE message_id=9105;") == 1);
            Check("mailbox delete only touches folder 0",
                ErrorOf(gm.DeleteMailboxMessage(MailCharacterOne, 9103)) == "邮件不存在或不属于该角色: 9103"
                && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_recipients WHERE message_id=9103 AND folder=1;") == 1);
        }
        private static void CheckMailboxClear(GmService gm, string dbPath)
        {
            var cleared = gm.ClearCharacterMailbox(MailCharacterOne);
            Check("mailbox clear purges every folder-0 row including saved and soft-deleted ones",
                IsSuccess(cleared)
                && GetIntProperty(cleared, "folder") == 0
                && GetIntProperty(cleared, "recipientCount") == 3
                && GetIntProperty(cleared, "messageCount") == 3
                && GetIntProperty(cleared, "attachmentCount") == 1
                && GetIntProperty(cleared, "auditCount") == 0
                && GetIntProperty(cleared, "campaignReferenceCount") == 0);
            Check("mailbox clear leaves the other folder and other characters alone",
                LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_messages;") == 2
                && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_recipients WHERE message_id=9103 AND folder=1;") == 1
                && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_recipients WHERE character_id=" + MailCharacterTwo + ";") == 1
                && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_attachments;") == 1);
            Check("mailbox clear leaves nothing listable for the character",
                GetIntProperty(gm.ListMailbox(MailCharacterOne, null), "count") == 0);

            var again = gm.ClearCharacterMailbox(MailCharacterOne);
            Check("second mailbox clear is idempotent",
                IsSuccess(again)
                && GetIntProperty(again, "recipientCount") == 0
                && GetIntProperty(again, "messageCount") == 0
                && GetIntProperty(again, "attachmentCount") == 0);
            Check("mailbox clear rejects an invalid or unknown character",
                ErrorOf(gm.ClearCharacterMailbox(0)) == "角色编号无效"
                && ErrorOf(gm.ClearCharacterMailbox(999999)) == "角色不存在或已删除: 999999");

            var lastRecipient = gm.DeleteMailboxMessage(MailCharacterTwo, 9102);
            Check("removing the last recipient finally drops the shared root message",
                IsSuccess(lastRecipient)
                && GetIntProperty(lastRecipient, "recipientCount") == 1
                && GetIntProperty(lastRecipient, "messageCount") == 1
                && GetIntProperty(lastRecipient, "attachmentCount") == 1
                && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_messages;") == 1
                && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_attachments;") == 0);
        }
        // ---------------- 第 3 层: 真实 PVF(整套发放 / 副职业 / 物品预览) ----------------

        private static void RunPvfTier()
        {
            var pvf = ResolveLatestServerPvf();
            // 缺 PVF 时按 FAIL 记一笔并跳过, 与其它 PVF 自测口径一致。
            Check("latest server PVF exists for the third tier", pvf != null);
            if (pvf == null)
                return;

            var dbPath = TempPath("dfogm-upstream-pvf-", ".db");
            try
            {
                var schema = Path.Combine(AppContext.BaseDirectory, "ServerCore", "Sqlite", "item_schema.sql");
                SqliteDatabaseBootstrap.CreateTestDatabase(dbPath, schema);
                SeedPvfTier(dbPath);
                Check("PVF tier loads a GM config from the temp database",
                    GmConfig.TryCreate(dbPath, pvf, out var config, out var configError), configError);
                if (config == null)
                    return;

                PvfArchiveAccessor.Configure(pvf);
                PvfRuntimeCache.ResetForPvfChange();
                GmService.ResetPvfStaticData();
                var index = new PvfIndexService(pvf);
                index.WarmInBackground();
                WaitForIndex(index);
                if (!index.IsReady)
                    return;

                var gm = new GmService(config, index);
                var probe = FindSendableSet(index);
                Check("PVF exposes at least one sendable equipment set", probe != null);
                if (probe != null)
                {
                    UpdateJob(dbPath, SetCharacter, probe.Job);
                    UpdateJob(dbPath, GbkNameCharacter, probe.Job);
                    CheckSetResolution(index, probe);
                    CheckSetGrant(gm, index, dbPath, probe);
                    CheckSetGrantRejections(gm, index, probe);
                }

                CheckExpertJobFlow(gm, pvf, dbPath);
                CheckItemPreviewAndIcon(index, probe);
            }
            finally
            {
                DeleteFile(dbPath);
            }
        }
        private static void SeedPvfTier(string dbPath)
        {
            using (var connection = Open(dbPath))
            using (var transaction = connection.BeginTransaction())
            {
                Exec(connection, transaction, $@"
INSERT INTO accounts(account_id,m_id,password_hash) VALUES
({PvfAccount},'upstream-pvf','');");
                // 820112 的名字按 GBK(936) BLOB 存, 用来压 GmSystemMailService 读名字的路径。
                Exec(connection, transaction, $@"
INSERT INTO characters(character_id,account_id,name,job,grow_type,level,exp,slot_index) VALUES
({SetCharacter},{PvfAccount},'upstream-set',0,0,70,0,0),
({GbkNameCharacter},{PvfAccount},CAST(X'BDC7C9ABD2BB' AS BLOB),0,0,70,0,1),
({ExpertCharacter},{PvfAccount},'upstream-expert',0,0,60,0,2);");
                transaction.Commit();
            }
        }

        private static void UpdateJob(string dbPath, int characterId, int job)
        {
            using (var connection = Open(dbPath))
            using (var transaction = connection.BeginTransaction())
            {
                Exec(connection, transaction,
                    $"UPDATE characters SET job={job} WHERE character_id={characterId};");
                transaction.Commit();
            }
        }

        private sealed class SetProbe
        {
            public int Job;
            public int SeedItemId;
            public string SetName;
            public List<int> MemberIds;
        }

        // 在 PVF 里找一个“真能整套发放”的套装: 部件数在 2..20 之间, 且该职业可用。
        private static SetProbe FindSendableSet(PvfIndexService index)
        {
            var scanned = 0;
            foreach (var entry in index.AllItems)
            {
                if (entry == null || entry.PartSetIndex <= 0)
                    continue;
                if (++scanned > 6000)
                    break;

                for (var job = 0; job <= 11; job++)
                {
                    if (!index.TryResolveSendableSet(entry.Id, job, out var members, out var setName, out _))
                        continue;
                    if (members == null || members.Count < 2)
                        continue;

                    return new SetProbe
                    {
                        Job = job,
                        SeedItemId = entry.Id,
                        SetName = setName,
                        MemberIds = new List<int>(members),
                    };
                }
            }

            return null;
        }
        private static PvfIndexService.ItemEntry FindEntry(PvfIndexService index, int itemId)
        {
            foreach (var entry in index.AllItems)
            {
                if (entry != null && entry.Id == itemId)
                    return entry;
            }
            return null;
        }

        private static void CheckSetResolution(PvfIndexService index, SetProbe probe)
        {
            var seed = FindEntry(index, probe.SeedItemId);
            Check("set seed item is present in the index", seed != null);
            if (seed == null)
                return;

            var members = probe.MemberIds;
            Check("set member count stays inside the 2..20 mail window",
                members.Count >= 2 && members.Count <= 20, "members=" + members.Count);
            Check("set members contain the seed item", members.Contains(probe.SeedItemId));
            Check("set members are distinct", members.Distinct().Count() == members.Count);
            Check("set name is not blank", !string.IsNullOrWhiteSpace(probe.SetName));
            Check("IsSetSendable agrees with TryResolveSendableSet", index.IsSetSendable(seed, probe.Job));

            var allSameSet = true;
            var allUsable = true;
            foreach (var id in members)
            {
                var member = FindEntry(index, id);
                if (member == null || member.PartSetIndex != seed.PartSetIndex)
                    allSameSet = false;
                if (member != null
                    && !string.IsNullOrWhiteSpace(member.UsableJob)
                    && !AvatarGrantPolicy.IsUsableByJob(member.UsableJob, probe.Job))
                    allUsable = false;
            }

            Check("every set member shares the seed part set index", allSameSet);
            Check("every set member is usable by the resolved job", allUsable);

            // 同一入参重复解析必须给出同一份成员表(索引已就绪, 结果不能抖)。
            Check("set resolution is stable across calls",
                index.TryResolveSendableSet(probe.SeedItemId, probe.Job, out var again, out _, out _)
                    && again.SequenceEqual(members));

            // 不按职业过滤时成员只会更多, 不会更少。
            if (index.TryResolveSendableSet(probe.SeedItemId, PvfIndexService.SetAnyJob, out var anyJob, out _, out _))
                Check("job filtering never widens the member list", anyJob.Count >= members.Count);

            var plain = index.AllItems.FirstOrDefault(entry => entry != null && entry.PartSetIndex <= 0);
            if (plain != null)
            {
                index.TryResolveSendableSet(plain.Id, probe.Job, out _, out _, out var plainError);
                Check("非套装物品被拒绝", plainError == "该物品不属于套装", plainError);
            }

            index.TryResolveSendableSet(999999999, probe.Job, out _, out _, out var missingError);
            Check("未知物品编号被拒绝", missingError == "PVF 中没有这件物品", missingError);
        }
        private static void CheckSetGrant(GmService gm, PvfIndexService index, string dbPath, SetProbe probe)
        {
            var members = probe.MemberIds;
            var idList = string.Join(",", members);
            var expectedMessages = (members.Count + 9) / 10;
            const string requestId = "gm:selftest-set-0001";
            var options = new ItemGrantOptions { QualityMode = ItemQualityMode.Top };

            var granted = gm.GiveItem(SetCharacter, probe.SeedItemId, 1, options, index, requestId, "mail", true);
            Check("整套发放成功", IsSuccess(granted), ErrorOf(granted));
            Check("整套发放走 mail_set 通道", GetStringProperty(granted, "delivery") == "mail_set");
            Check("整套附件数等于成员数",
                GetIntProperty(granted, "attachmentCount") == members.Count,
                "attachmentCount=" + GetIntProperty(granted, "attachmentCount"));
            Check("整套邮件数按 10 件分片且不超过 2 封",
                GetIntProperty(granted, "messageCount") == expectedMessages && expectedMessages <= 2,
                "messageCount=" + GetIntProperty(granted, "messageCount"));
            Check("整套发放回填套装名", !string.IsNullOrWhiteSpace(GetStringProperty(granted, "setName")));
            Check("首次发放不是重放", GetBoolProperty(granted, "replayed") == false);

            Check("整套邮件按分片落库",
                LoadInt(dbPath, $"SELECT COUNT(*) FROM mailbox_messages WHERE receiver_character_id={SetCharacter};")
                    == expectedMessages);
            Check("整套邮件标题是 GM套装发放",
                LoadText(dbPath, $"SELECT title FROM mailbox_messages WHERE receiver_character_id={SetCharacter} LIMIT 1;")
                    == "GM套装发放");
            // 幂等键是 "gm:" + 请求编号, 第二封分片再追加 :part:1。
            var rootKey = "gm:" + requestId;
            Check("幂等键按 requestId 派生",
                LoadInt(dbPath, $"SELECT COUNT(*) FROM mailbox_messages WHERE idempotency_key='{rootKey}';") == 1
                    && LoadInt(dbPath, $"SELECT COUNT(*) FROM mailbox_messages WHERE idempotency_key LIKE '{rootKey}%';")
                        == expectedMessages
                    && LoadInt(dbPath, $"SELECT COUNT(*) FROM mailbox_messages WHERE idempotency_key='{rootKey}:part:1';")
                        == expectedMessages - 1);
            Check("附件总数等于成员数",
                LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_attachments;") == members.Count);
            Check("附件物品编号与解析出的成员一致",
                LoadInt(dbPath, "SELECT COUNT(DISTINCT item_template_id) FROM mailbox_attachments;") == members.Count
                    && LoadInt(dbPath, $"SELECT COUNT(*) FROM mailbox_attachments WHERE item_template_id NOT IN ({idList});") == 0);
            // 每封邮件的附件序号都从 0 开始, 不能跨邮件连号。
            Check("附件序号按邮件各自从 0 起排",
                LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_attachments WHERE ordinal=0;") == expectedMessages
                    && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_attachments WHERE ordinal>=10;") == 0);
            Check("整套每个部件固定 1 件",
                LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_attachments WHERE item_count<>1;") == 0);
            Check("整套附件写满 99 字节 ItemCore",
                LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_attachments WHERE item_core IS NULL OR LENGTH(item_core)<>99;") == 0);

            var replay = gm.GiveItem(SetCharacter, probe.SeedItemId, 1, options, index, requestId, "mail", true);
            Check("同一请求编号重放被识别",
                IsSuccess(replay) && GetBoolProperty(replay, "replayed"), ErrorOf(replay));
            Check("重放不追加邮件",
                LoadInt(dbPath, $"SELECT COUNT(*) FROM mailbox_messages WHERE receiver_character_id={SetCharacter};")
                    == expectedMessages
                    && LoadInt(dbPath, "SELECT COUNT(*) FROM mailbox_attachments;") == members.Count);
        }
        private static void CheckSetGrantRejections(GmService gm, PvfIndexService index, SetProbe probe)
        {
            var top = new ItemGrantOptions { QualityMode = ItemQualityMode.Top };
            // 请求哈希把 QualityMode 也算进去, 同编号换配置必须被判成冲突而不是重放。
            var conflict = gm.GiveItem(
                SetCharacter, probe.SeedItemId, 1,
                new ItemGrantOptions { QualityMode = ItemQualityMode.Random },
                index, "gm:selftest-set-0001", "mail", true);
            Check("同编号不同配置被判冲突",
                ErrorOf(conflict) == "同一请求编号已用于不同的发放内容，请刷新页面后重试", ErrorOf(conflict));

            var inventory = gm.GiveItem(
                SetCharacter, probe.SeedItemId, 1, top, index, "gm:selftest-set-0002", "inventory", true);
            Check("整套发放拒绝直塞背包",
                ErrorOf(inventory) == "整套发放只支持邮件，请把发放方式改为邮件", ErrorOf(inventory));

            var many = gm.GiveItem(
                SetCharacter, probe.SeedItemId, 2, top, index, "gm:selftest-set-0003", "mail", true);
            Check("整套发放拒绝数量不为 1",
                ErrorOf(many) == "整套发放每个部件固定发 1 件，请把数量改为 1", ErrorOf(many));

            var shortId = gm.GiveItem(SetCharacter, probe.SeedItemId, 1, top, index, "gm:1", "mail", true);
            Check("整套发放拒绝过短的请求编号",
                ErrorOf(shortId) == "发放请求编号无效，请刷新页面后重试", ErrorOf(shortId));

            var noCharacter = gm.GiveItem(999999, probe.SeedItemId, 1, top, index, "gm:selftest-set-0004", "mail", true);
            Check("整套发放拒绝不存在的角色",
                ErrorOf(noCharacter) == "角色不存在: 999999", ErrorOf(noCharacter));

            var plain = index.AllItems.FirstOrDefault(entry => entry != null && entry.PartSetIndex <= 0);
            if (plain != null)
            {
                var notSet = gm.GiveItem(SetCharacter, plain.Id, 1, top, index, "gm:selftest-set-0005", "mail", true);
                Check("整套发放拒绝非套装物品", ErrorOf(notSet) == "该物品不属于套装", ErrorOf(notSet));
            }

            // 锻造/增幅这类只有部分部件支持的配置, 应该按部件能力降级而不是整单失败。
            var tuned = gm.GiveItem(
                SetCharacter, probe.SeedItemId, 1,
                new ItemGrantOptions { QualityMode = ItemQualityMode.Top, UpgradeLevel = 11, AmplifyType = 1, ForgingLevel = 4 },
                index, "gm:selftest-set-0006", "mail", true);
            Check("部分部件不支持的配置按部件降级发放",
                IsSuccess(tuned) && GetIntProperty(tuned, "attachmentCount") == probe.MemberIds.Count,
                ErrorOf(tuned));

            // 角色名是 GBK BLOB 时, 邮件服务读名字不能炸(上游按字符串读会抛)。
            var gbk = TryGrant(gm, index, GbkNameCharacter, probe.SeedItemId, top, "gm:selftest-set-gbk1", out var gbkError);
            Check("GBK BLOB 角色名不影响整套发放", gbk != null && IsSuccess(gbk), gbkError ?? ErrorOf(gbk));
        }

        private static object TryGrant(
            GmService gm,
            PvfIndexService index,
            int characterId,
            int itemId,
            ItemGrantOptions options,
            string requestId,
            out string error)
        {
            error = null;
            try
            {
                return gm.GiveItem(characterId, itemId, 1, options, index, requestId, "mail", true);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }
        private static int RecipeCount(ExpertJobDefinition definition, uint exp)
        {
            return definition.GetAutoLearnRecipeIds(exp).Distinct().Count();
        }

        private static int SkillIdCount(string dbPath, int characterId, IEnumerable<int> skillIds)
        {
            var ids = (skillIds ?? Enumerable.Empty<int>()).Distinct().ToList();
            if (ids.Count == 0)
                return 0;
            return LoadInt(
                dbPath,
                $"SELECT COUNT(DISTINCT skill_id) FROM character_skills WHERE character_id={characterId}"
                    + $" AND skill_id IN ({string.Join(",", ids)});");
        }

        private static List<int> GrantIds(ExpertJobDefinition definition)
        {
            return definition == null
                ? new List<int>()
                : definition.SkillGrants.Select(grant => (int)grant.SkillId).Distinct().ToList();
        }

        private static void CheckExpertJobFlow(GmService gm, string pvf, string dbPath)
        {
            ExpertJobPvfData data;
            IReadOnlyList<ExpertJobDefinition> jobs;
            try
            {
                data = new ExpertJobPvfData(pvf);
                jobs = data.All;
            }
            catch (Exception ex)
            {
                // .exj 缺失时整个副职业面板不可用, 直接记 FAIL 把 PVF 内容问题暴露出来。
                Check("PVF 提供副职业定义(.exj)", false, ex.Message);
                return;
            }

            Check("PVF 至少解析出两个副职业定义", jobs.Count >= 2, "count=" + jobs.Count);
            if (jobs.Count == 0)
                return;

            var primary = jobs.FirstOrDefault(job => job.Type == ExpertJobPvfData.DisjointerType) ?? jobs[0];
            var secondary = jobs.FirstOrDefault(job => job.Type != primary.Type);

            var initial = gm.GetExpertJob(ExpertCharacter);
            Check("副职业快照可读", IsSuccess(initial), ErrorOf(initial));
            Check("未选副职业时类型为 0 且名字是无副职业",
                GetIntProperty(initial, "type") == 0 && GetStringProperty(initial, "typeName") == "无副职业");
            Check("未选副职业时等级/经验/配方都是 0",
                GetIntProperty(initial, "level") == 0
                    && GetLongProperty(initial, "exp") == 0
                    && GetIntProperty(initial, "learnedRecipeCount") == 0
                    && GetIntProperty(initial, "maxLevel") == 0);
            var options = GetProperty(initial, "options") as System.Collections.IEnumerable;
            var optionList = options == null ? new List<object>() : options.Cast<object>().ToList();
            Check("副职业下拉包含无副职业占位 + 全部 PVF 定义",
                optionList.Count == jobs.Count + 1 && GetIntProperty(optionList.FirstOrDefault(), "type") == 0,
                "options=" + optionList.Count);

            var levelOne = gm.SetExpertJob(ExpertCharacter, primary.Type, 1, null);
            Check("设置副职业等级 1 成功", IsSuccess(levelOne), ErrorOf(levelOne));
            Check("副职业类型/名称按 PVF 回填",
                GetIntProperty(levelOne, "type") == primary.Type
                    && GetStringProperty(levelOne, "typeName") == primary.Name);
            Check("副职业上限来自 .exj",
                GetIntProperty(levelOne, "maxLevel") == primary.MaxLevel
                    && GetLongProperty(levelOne, "maxExp") == primary.MaxExp);
            Check("等级 1 对应经验 0",
                GetIntProperty(levelOne, "level") == 1 && GetLongProperty(levelOne, "exp") == 0);
            Check("等级 1 的自动配方按 .exj 落库",
                GetIntProperty(levelOne, "learnedRecipeCount") == RecipeCount(primary, 0)
                    && LoadInt(dbPath, $"SELECT COUNT(*) FROM character_expert_job_recipes WHERE character_id={ExpertCharacter};")
                        == RecipeCount(primary, 0));
            Check("副职业类型写入 character_subtype0_fields",
                LoadInt(dbPath, $"SELECT expert_job_type FROM character_subtype0_fields WHERE character_id={ExpertCharacter};")
                    == primary.Type);
            // 等级 → 经验 → 等级 必须闭环, 否则面板上调完等级再刷新就会跳级。
            var roundTripOk = true;
            var recipeGrowthOk = true;
            var lastRecipes = -1;
            var probeLevels = Math.Min(primary.MaxLevel, 12);
            for (var level = 1; level <= probeLevels; level++)
            {
                var result = gm.SetExpertJob(ExpertCharacter, primary.Type, level, null);
                if (!IsSuccess(result)
                    || GetIntProperty(result, "level") != level
                    || GetLongProperty(result, "exp") != primary.GetExpForLevel(level))
                    roundTripOk = false;
                var recipes = GetIntProperty(result, "learnedRecipeCount");
                if (recipes != RecipeCount(primary, primary.GetExpForLevel(level)) || recipes < lastRecipes)
                    recipeGrowthOk = false;
                lastRecipes = recipes;
            }

            Check("副职业等级/经验换算闭环", roundTripOk, "levels=" + probeLevels);
            Check("配方数量随等级单调增长且与 .exj 一致", recipeGrowthOk);

            var clamped = gm.SetExpertJob(ExpertCharacter, primary.Type, null, (long)primary.MaxExp + 100000);
            Check("超上限经验被夹到 MaxExp",
                IsSuccess(clamped)
                    && GetLongProperty(clamped, "exp") == primary.MaxExp
                    && GetIntProperty(clamped, "level") == primary.MaxLevel,
                ErrorOf(clamped));

            var negative = gm.SetExpertJob(ExpertCharacter, primary.Type, null, -1);
            Check("负经验被拒绝", ErrorOf(negative) == "副职业经验不能为负数。", ErrorOf(negative));

            var zeroLevel = gm.SetExpertJob(ExpertCharacter, primary.Type, 0, null);
            Check("等级 0 被拒绝",
                ErrorOf(zeroLevel) == "副职业等级范围 1-" + primary.MaxLevel + "。", ErrorOf(zeroLevel));

            var overLevel = gm.SetExpertJob(ExpertCharacter, primary.Type, primary.MaxLevel + 1, null);
            Check("超上限等级被拒绝",
                ErrorOf(overLevel) == "副职业等级范围 1-" + primary.MaxLevel + "。", ErrorOf(overLevel));

            var unknownType = 250;
            while (jobs.Any(job => job.Type == unknownType) && unknownType > 5)
                unknownType--;
            var unknown = gm.SetExpertJob(ExpertCharacter, unknownType, 1, null);
            Check("未注册的副职业类型被拒绝",
                ErrorOf(unknown) == "未知的副职业类型。", ErrorOf(unknown));

            var badType = gm.SetExpertJob(ExpertCharacter, -1, 1, null);
            Check("负副职业类型被拒绝", ErrorOf(badType) == "副职业类型无效。", ErrorOf(badType));

            var noCharacter = gm.SetExpertJob(999999, primary.Type, 1, null);
            Check("副职业写入拒绝不存在的角色", ErrorOf(noCharacter) == "角色不存在: 999999", ErrorOf(noCharacter));

            var maxed = gm.MaxExpertJob(ExpertCharacter, null);
            Check("一键满级沿用当前副职业并顶满经验",
                IsSuccess(maxed)
                    && GetIntProperty(maxed, "type") == primary.Type
                    && GetLongProperty(maxed, "exp") == primary.MaxExp
                    && GetIntProperty(maxed, "level") == primary.MaxLevel,
                ErrorOf(maxed));
            Check("一键满级后配方数等于满级配方数",
                GetIntProperty(maxed, "learnedRecipeCount") == RecipeCount(primary, primary.MaxExp)
                    && LoadInt(dbPath, $"SELECT COUNT(*) FROM character_expert_job_recipes WHERE character_id={ExpertCharacter};")
                        == RecipeCount(primary, primary.MaxExp));
            if (primary.Type == ExpertJobPvfData.DisjointerType)
            {
                var expectedGrade = Math.Max(1, primary.MaxMachineGrade);
                Check("分解师一键满级顶满分解机等级/耐久",
                    GetIntProperty(maxed, "machineGrade") == expectedGrade
                        && GetIntProperty(maxed, "machineEndurance") == primary.GetEnduranceCap(expectedGrade)
                        && GetIntProperty(maxed, "maxMachineGrade") == primary.MaxMachineGrade);
            }
            var primaryGrants = GrantIds(primary);
            if (primaryGrants.Count > 0)
                Check("副职业送技已并入技能页",
                    SkillIdCount(dbPath, ExpertCharacter, primaryGrants) == primaryGrants.Count,
                    "present=" + SkillIdCount(dbPath, ExpertCharacter, primaryGrants));

            if (secondary != null)
            {
                var secondaryGrants = GrantIds(secondary);
                var switched = gm.SetExpertJob(ExpertCharacter, secondary.Type, null, null);
                Check("切换副职业成功", IsSuccess(switched), ErrorOf(switched));
                Check("切换副职业后经验归零",
                    GetIntProperty(switched, "type") == secondary.Type
                        && GetLongProperty(switched, "exp") == 0);
                Check("切换副职业后配方按新职业重建",
                    GetIntProperty(switched, "learnedRecipeCount") == RecipeCount(secondary, 0)
                        && LoadInt(dbPath, $"SELECT COUNT(*) FROM character_expert_job_recipes WHERE character_id={ExpertCharacter};")
                            == RecipeCount(secondary, 0));

                var droppedGrants = primaryGrants.Except(secondaryGrants).ToList();
                if (droppedGrants.Count > 0)
                    Check("切换副职业后旧送技被摘掉",
                        SkillIdCount(dbPath, ExpertCharacter, droppedGrants) == 0,
                        "left=" + SkillIdCount(dbPath, ExpertCharacter, droppedGrants));
                if (secondaryGrants.Count > 0)
                    Check("切换副职业后新送技被并入",
                        SkillIdCount(dbPath, ExpertCharacter, secondaryGrants) == secondaryGrants.Count);

                var cleared = gm.SetExpertJob(ExpertCharacter, 0, null, null);
                Check("清空副职业成功", IsSuccess(cleared), ErrorOf(cleared));
                Check("清空副职业后类型/等级/经验/配方全部归零",
                    GetIntProperty(cleared, "type") == 0
                        && GetIntProperty(cleared, "level") == 0
                        && GetLongProperty(cleared, "exp") == 0
                        && GetIntProperty(cleared, "learnedRecipeCount") == 0
                        && GetStringProperty(cleared, "typeName") == "无副职业");
                Check("清空副职业后配方表被清空",
                    LoadInt(dbPath, $"SELECT COUNT(*) FROM character_expert_job_recipes WHERE character_id={ExpertCharacter};") == 0);
                if (secondaryGrants.Count > 0)
                    Check("清空副职业后送技被摘掉",
                        SkillIdCount(dbPath, ExpertCharacter, secondaryGrants) == 0);

                var maxWithoutJob = gm.MaxExpertJob(ExpertCharacter, null);
                Check("没有副职业时一键满级被拒绝",
                    ErrorOf(maxWithoutJob) == "请先选择副职业再一键满级。", ErrorOf(maxWithoutJob));

                var maxWithType = gm.MaxExpertJob(ExpertCharacter, secondary.Type);
                Check("指定类型的一键满级直接顶满",
                    IsSuccess(maxWithType)
                        && GetIntProperty(maxWithType, "type") == secondary.Type
                        && GetLongProperty(maxWithType, "exp") == secondary.MaxExp
                        && GetIntProperty(maxWithType, "level") == secondary.MaxLevel,
                    ErrorOf(maxWithType));
            }
        }

        private static void CheckItemPreviewAndIcon(PvfIndexService index, SetProbe probe)
        {
            var missing = index.GetItemPreview(999999999);
            Check("未知物品预览被拒绝",
                !IsSuccess(missing) && ErrorOf(missing) == "PVF 中没有这件物品", ErrorOf(missing));

            if (probe == null)
                return;

            var preview = index.GetItemPreview(probe.SeedItemId);
            Check("套装物品预览成功", IsSuccess(preview), ErrorOf(preview));
            var set = GetProperty(preview, "set");
            Check("套装物品预览带套装区块", set != null);
            if (set != null)
            {
                var pieces = GetProperty(set, "pieces") as System.Collections.IEnumerable;
                var pieceCount = pieces == null ? 0 : pieces.Cast<object>().Count();
                // 预览不按职业过滤, 件数只会 >= 发放解析出的成员数。
                Check("套装预览件数不少于可发放成员数",
                    pieceCount >= probe.MemberIds.Count, "pieces=" + pieceCount);
                Check("套装预览带套装名", !string.IsNullOrWhiteSpace(GetStringProperty(set, "name")));
            }

            var withIcon = index.AllItems.FirstOrDefault(
                entry => entry != null && !string.IsNullOrWhiteSpace(entry.IconPath));
            Check("PVF 里存在带图标的物品", withIcon != null);
            if (withIcon != null)
            {
                Check("图标路径可解析",
                    index.TryGetIcon(withIcon.Id, out var iconPath, out var iconFrame, out _, out _)
                        && !string.IsNullOrWhiteSpace(iconPath)
                        && iconFrame >= 0);
            }

            Check("未知物品没有图标", !index.TryGetIcon(999999999, out _, out _, out _, out _));
        }
        // ---------------- 夹具: NPK ----------------

        private static byte[] BuildNameKey()
        {
            var prefix = Encoding.ASCII.GetBytes("puchikon@neople dungeon and fighter ");
            var key = new byte[256];
            Buffer.BlockCopy(prefix, 0, key, 0, prefix.Length);
            var dnf = Encoding.ASCII.GetBytes("DNF");
            for (var i = prefix.Length; i < key.Length; i++)
                key[i] = dnf[(i - prefix.Length) % dnf.Length];
            return key;
        }

        private static byte[] EncryptName(string name)
        {
            var key = BuildNameKey();
            var plain = new byte[256];
            var raw = Encoding.ASCII.GetBytes(name ?? string.Empty);
            Buffer.BlockCopy(raw, 0, plain, 0, Math.Min(raw.Length, plain.Length));
            var encrypted = new byte[256];
            for (var i = 0; i < encrypted.Length; i++)
                encrypted[i] = (byte)(plain[i] ^ key[i]);
            return encrypted;
        }

        // NeoplePack_Bill: 20 字节头(15 字节魔数 + 表项数@16) + 264 字节表项 + 顺序拼接的负载。
        private static byte[] BuildNpk(params (string Name, byte[] Data)[] entries)
        {
            var tableOffset = 20;
            var payloadOffset = tableOffset + entries.Length * 264;
            var total = payloadOffset + entries.Sum(entry => entry.Data.Length);
            var buffer = new byte[total];
            var magic = Encoding.ASCII.GetBytes("NeoplePack_Bill");
            Buffer.BlockCopy(magic, 0, buffer, 0, magic.Length);
            WriteInt32(buffer, 16, entries.Length);

            var cursor = payloadOffset;
            for (var i = 0; i < entries.Length; i++)
            {
                var row = tableOffset + i * 264;
                WriteInt32(buffer, row, cursor);
                WriteInt32(buffer, row + 4, entries[i].Data.Length);
                Buffer.BlockCopy(EncryptName(entries[i].Name), 0, buffer, row + 8, 256);
                Buffer.BlockCopy(entries[i].Data, 0, buffer, cursor, entries[i].Data.Length);
                cursor += entries[i].Data.Length;
            }

            return buffer;
        }
        private static bool TryOpenBytes(string directory, string fileName, byte[] bytes)
        {
            return TryOpenBytes(directory, fileName, bytes, out _);
        }

        private static bool TryOpenBytes(string directory, string fileName, byte[] bytes, out NpkArchive archive)
        {
            var path = Path.Combine(directory, fileName);
            File.WriteAllBytes(path, bytes);
            return NpkArchive.TryOpen(path, out archive);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        // ---------------- 夹具: IMG v2 ----------------

        private sealed class ImgFrame
        {
            public int Type;
            public int Compressed;
            public int Width;
            public int Height;
            public byte[] Payload = Array.Empty<byte>();
            public int KeyX;
            public int KeyY;
            public int MaxWidth;
            public int MaxHeight;
            public bool IsLink;
            public int LinkIndex;
        }

        private static ImgFrame PixelFrame(int type, int width, int height, byte[] payload, int compressed = 0)
        {
            return new ImgFrame
            {
                Type = type,
                Compressed = compressed,
                Width = width,
                Height = height,
                Payload = payload ?? Array.Empty<byte>(),
            };
        }

        private static ImgFrame Argb8888Frame(int width, int height, byte[] bgra)
        {
            return PixelFrame(0x10, width, height, bgra);
        }

        private static ImgFrame LinkFrame(int linkIndex)
        {
            return new ImgFrame { Type = 0x11, IsLink = true, LinkIndex = linkIndex };
        }
        // 32 字节头(魔数 + 索引长度@16 + 版本@24 + 帧数@28) + 帧表(像素帧 36B / 链接帧 8B) + 像素负载。
        private static byte[] BuildImg(ImgFrame[] frames, string magic = "Neople Img File", int version = 2)
        {
            var tableSize = frames.Sum(frame => frame.IsLink ? 8 : 36);
            var payloadSize = frames.Sum(frame => frame.IsLink ? 0 : frame.Payload.Length);
            var buffer = new byte[32 + tableSize + payloadSize];
            var magicBytes = Encoding.ASCII.GetBytes(magic ?? string.Empty);
            Buffer.BlockCopy(magicBytes, 0, buffer, 0, Math.Min(magicBytes.Length, 16));
            WriteInt32(buffer, 16, tableSize);
            WriteInt32(buffer, 24, version);
            WriteInt32(buffer, 28, frames.Length);

            var row = 32;
            var payload = 32 + tableSize;
            foreach (var frame in frames)
            {
                if (frame.IsLink)
                {
                    WriteInt32(buffer, row, frame.Type);
                    WriteInt32(buffer, row + 4, frame.LinkIndex);
                    row += 8;
                    continue;
                }

                WriteInt32(buffer, row, frame.Type);
                WriteInt32(buffer, row + 4, frame.Compressed);
                WriteInt32(buffer, row + 8, frame.Width);
                WriteInt32(buffer, row + 12, frame.Height);
                WriteInt32(buffer, row + 16, frame.Payload.Length);
                WriteInt32(buffer, row + 20, frame.KeyX);
                WriteInt32(buffer, row + 24, frame.KeyY);
                WriteInt32(buffer, row + 28, frame.MaxWidth);
                WriteInt32(buffer, row + 32, frame.MaxHeight);
                Buffer.BlockCopy(frame.Payload, 0, buffer, payload, frame.Payload.Length);
                payload += frame.Payload.Length;
                row += 36;
            }

            return buffer;
        }

        private static byte[] Deflate(byte[] raw)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw, 0, raw.Length);
            return output.ToArray();
        }

        private static byte[] PixelAt(byte[] rgba, int width, int x, int y)
        {
            var offset = (y * width + x) * 4;
            return new[] { rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3] };
        }
        // ---------------- 夹具: PNG 解析(校验 CRC32 + 反 filter) ----------------

        private static bool TryParsePng(byte[] png, out int width, out int height, out byte[] rgba, out string error)
        {
            width = 0;
            height = 0;
            rgba = null;
            error = null;
            if (png == null || png.Length < 8 || !png.Take(8).SequenceEqual(PngSignature))
            {
                error = "签名不对";
                return false;
            }

            var idat = new MemoryStream();
            var sawIhdr = false;
            var sawIend = false;
            var pos = 8;
            while (pos + 8 <= png.Length)
            {
                var length = ReadBigEndian(png, pos);
                var type = Encoding.ASCII.GetString(png, pos + 4, 4);
                if (length < 0 || pos + 12 + length > png.Length)
                {
                    error = "分块 " + type + " 长度越界";
                    return false;
                }

                var data = new byte[length];
                Buffer.BlockCopy(png, pos + 8, data, 0, length);
                var expected = PngCrc(Encoding.ASCII.GetBytes(type), data);
                var actual = (uint)ReadBigEndian(png, pos + 8 + length);
                if (expected != actual)
                {
                    error = "分块 " + type + " CRC32 不匹配";
                    return false;
                }

                if (type == "IHDR")
                {
                    width = ReadBigEndian(data, 0);
                    height = ReadBigEndian(data, 4);
                    if (data[8] != 8 || data[9] != 6)
                    {
                        error = "IHDR 不是 8bit/RGBA";
                        return false;
                    }
                    sawIhdr = true;
                }
                else if (type == "IDAT")
                {
                    idat.Write(data, 0, data.Length);
                }
                else if (type == "IEND")
                {
                    sawIend = true;
                }

                pos += 12 + length;
            }
            if (!sawIhdr || !sawIend || width <= 0 || height <= 0)
            {
                error = "缺少 IHDR/IEND";
                return false;
            }

            byte[] raw;
            using (var input = new MemoryStream(idat.ToArray()))
            using (var zlib = new ZLibStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                zlib.CopyTo(output);
                raw = output.ToArray();
            }

            var stride = width * 4;
            if (raw.Length != height * (1 + stride))
            {
                error = "解压后的扫描线长度不对: " + raw.Length;
                return false;
            }

            var pixels = new byte[height * stride];
            for (var y = 0; y < height; y++)
            {
                if (raw[y * (1 + stride)] != 0)
                {
                    error = "第 " + y + " 行不是 filter 0";
                    return false;
                }
                Buffer.BlockCopy(raw, y * (1 + stride) + 1, pixels, y * stride, stride);
            }

            rgba = pixels;
            return true;
        }

        private static int ReadBigEndian(byte[] buffer, int offset)
        {
            return (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
        }

        private static uint PngCrc(byte[] type, byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in type.Concat(data))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
            return crc ^ 0xFFFFFFFFu;
        }

        private static bool ThrowsArgumentException(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
        // ---------------- 夹具: 临时文件 / 反射 / 数据库 ----------------

        private static string TempPath(string prefix, string extension)
        {
            return Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + extension);
        }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteFile(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (path != null && Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static List<object> MailsOf(object listed)
        {
            var mails = GetProperty(listed, "mails") as System.Collections.IEnumerable;
            return mails == null ? new List<object>() : mails.Cast<object>().ToList();
        }

        private static List<object> AttachmentsOf(object mail)
        {
            var attachments = GetProperty(mail, "attachments") as System.Collections.IEnumerable;
            return attachments == null ? new List<object>() : attachments.Cast<object>().ToList();
        }
        private static object GetProperty(object target, string name)
        {
            if (target == null)
                return null;
            var property = target.GetType().GetProperty(
                name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property?.GetValue(target);
        }

        private static int GetIntProperty(object target, string name)
        {
            var value = GetProperty(target, name);
            return value == null ? 0 : Convert.ToInt32(value);
        }

        private static long GetLongProperty(object target, string name)
        {
            var value = GetProperty(target, name);
            return value == null ? 0L : Convert.ToInt64(value);
        }

        private static string GetStringProperty(object target, string name)
        {
            return GetProperty(target, name) as string;
        }

        private static bool GetBoolProperty(object target, string name)
        {
            var value = GetProperty(target, name);
            return value != null && Convert.ToBoolean(value);
        }

        private static bool IsSuccess(object result)
        {
            return GetBoolProperty(result, "success");
        }

        private static string ErrorOf(object result)
        {
            return GetStringProperty(result, "error");
        }

        private static int LoadInt(string dbPath, string sql)
        {
            using var connection = Open(dbPath);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
        }

        private static string LoadText(string dbPath, string sql)
        {
            using var connection = Open(dbPath);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }
        private static void Exec(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static SqliteConnection Open(string dbPath)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
            connection.Open();
            return connection;
        }

        private static void WaitForIndex(PvfIndexService index)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!index.IsReady && string.IsNullOrWhiteSpace(index.BuildError) && DateTime.UtcNow < deadline)
                Thread.Sleep(100);
            Check("PVF index ready", index.IsReady && string.IsNullOrWhiteSpace(index.BuildError), index.BuildError);
        }

        private static string ResolveLatestServerPvf()
        {
            return SelfTestPvfLocator.ResolveLatestServerPvf();
        }

        private static void Check(string name, bool condition, string error = null)
        {
            if (condition)
            {
                Console.WriteLine("PASS " + name);
                return;
            }
            _failures++;
            Console.Error.WriteLine("FAIL " + name + (string.IsNullOrWhiteSpace(error) ? string.Empty : ": " + error));
        }
    }
}
