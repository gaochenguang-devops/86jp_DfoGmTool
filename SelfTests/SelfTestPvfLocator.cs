using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoGmTool.SelfTests
{
    // 几个 PVF 相关自测原来各自复制一份定位逻辑, 集中到这里。
    // 顺序: DFO_GM_SELFTEST_PVF 覆盖 > 服务端仓库布局(Codes/ServerS4A21_*) > 工作目录/上级目录里的散装 Script.pvf。
    // 保持"服务端布局优先"的老行为, 散装路径只是兜底, 让没有服务端仓库的机器也能跑完自测。
    internal static class SelfTestPvfLocator
    {
        internal const string OverrideEnvironmentVariable = "DFO_GM_SELFTEST_PVF";

        internal static string ResolveLatestServerPvf()
        {
            var overridePath = ResolveOverride();
            if (overridePath != null)
                return overridePath;

            var roots = EnumerateSearchRoots();
            foreach (var root in roots)
            {
                var fromServerRepository = ResolveFromCodesRoot(Path.Combine(root, "Codes"));
                if (fromServerRepository != null)
                    return fromServerRepository;
            }

            foreach (var root in roots)
            {
                var loose = ResolveLoose(root);
                if (loose != null)
                    return loose;
            }

            return null;
        }

        // 环境变量既可以直接指到 Script.pvf, 也可以指到装着它的目录。
        private static string ResolveOverride()
        {
            var configured = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configured))
                return null;

            string full;
            try
            {
                full = Path.GetFullPath(configured.Trim().Trim('"'));
            }
            catch (Exception)
            {
                return null;
            }

            if (File.Exists(full))
                return full;
            if (!Directory.Exists(full))
                return null;
            return ResolveLoose(full);
        }

        private static string ResolveFromCodesRoot(string codesRoot)
        {
            if (!Directory.Exists(codesRoot))
                return null;

            var exact = Path.Combine(codesRoot, "ServerS4A21_git", "Server", "DfoServer", "Data", "Pvf", "Script.pvf");
            if (File.Exists(exact))
                return exact;

            string[] serverDirectories;
            try
            {
                serverDirectories = Directory.GetDirectories(codesRoot, "ServerS4A21_*");
            }
            catch (Exception)
            {
                return null;
            }

            foreach (var serverDir in serverDirectories.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var path in new[]
                {
                    Path.Combine(serverDir, "dist", "linux-x64", "Data", "Pvf", "Script.pvf"),
                    Path.Combine(serverDir, "dist", "win-x64", "Data", "Pvf", "Script.pvf"),
                    Path.Combine(serverDir, "Server", "DfoServer", "bin", "Release", "win-x64", "Data", "Pvf", "Script.pvf"),
                    Path.Combine(serverDir, "Server", "DfoServer", "bin", "Release", "linux-x64", "Data", "Pvf", "Script.pvf"),
                    Path.Combine(serverDir, "Server", "DfoServer", "bin", "Debug", "Data", "Pvf", "Script.pvf"),
                    Path.Combine(serverDir, "Server", "DfoServer", "Data", "Pvf", "Script.pvf"),
                })
                {
                    if (File.Exists(path))
                        return path;
                }
            }

            return null;
        }

        private static string ResolveLoose(string root)
        {
            foreach (var path in new[]
            {
                Path.Combine(root, "Script.pvf"),
                Path.Combine(root, "Data", "Pvf", "Script.pvf"),
                Path.Combine(root, "dist", "win-x64", "Data", "Pvf", "Script.pvf"),
                Path.Combine(root, "dist", "linux-x64", "Data", "Pvf", "Script.pvf"),
            })
            {
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        private static List<string> EnumerateSearchRoots()
        {
            var roots = new List<string>();
            AddRoot(roots, Directory.GetCurrentDirectory());
            AddRoot(roots, AppContext.BaseDirectory);

            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var directory = SafeDirectory(start);
                for (var depth = 0; depth < 8 && directory != null; depth++, directory = directory.Parent)
                    AddRoot(roots, directory.FullName);
            }

            return roots;
        }

        private static DirectoryInfo SafeDirectory(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? null : new DirectoryInfo(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void AddRoot(List<string> roots, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return;
            }
            if (!roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                roots.Add(path);
        }
    }
}
