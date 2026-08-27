# Linux 部署

在安装了 .NET 10 SDK 的 Linux 构建机执行：

```bash
chmod +x deploy/linux/build.sh
./deploy/linux/build.sh
# 若目标机已有 .NET 10 Runtime，可改为框架依赖发布：
./deploy/linux/build.sh --framework-dependent
```

默认发布目录为 `dist/`。将其内容复制到服务器的 `/srv/86jpgmtool/dist/`，并在该目录编辑 `config.ini`。服务单元按现有服务器约定以 `root` 运行；自包含发布后确保主程序可执行：`chmod +x /srv/86jpgmtool/dist/DfoGmTool`。

```bash
sudo install -m 0644 deploy/linux/dfo-gm-tool.service /etc/systemd/system/dfo-gm-tool.service
sudo systemctl daemon-reload
sudo systemctl enable --now dfo-gm-tool
sudo systemctl status dfo-gm-tool
```

默认单元直接启动自包含产物。若用 `--framework-dependent` 发布，请将 `ExecStart` 改为：

```ini
ExecStart=/usr/bin/dotnet /srv/86jpgmtool/dist/DfoGmTool.dll
```

修改单元文件后，执行 `sudo systemctl daemon-reload && sudo systemctl restart dfo-gm-tool`。查看日志使用 `journalctl -u dfo-gm-tool -f`。
