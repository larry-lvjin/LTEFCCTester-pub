# LTE FCC TxPower

> **Band 26 传导发射功率自动测试工具** — 基于 R&S CMW500 综测仪的 LTE UE 最大传导功率（Conducted TX Power）自动化测试上位机，面向 FCC 认证摸底。

一个 Windows WPF 桌面程序（.NET 10 / C#）。通过**原生 TCP Socket（端口 5025）**直连罗德与施瓦茨 CMW500，无需安装 NI-VISA 或任何 IO 库，即可在多带宽 / 多信道 / 多调制 / 多 RB 分配组合下逐条测量 UE 的传导发射功率并自动判定 PASS/FAIL，最终导出 Excel 报告。

---

## 目录

- [功能特性](#功能特性)
- [测试模型](#测试模型)
- [运行环境](#运行环境)
- [快速开始](#快速开始)
- [使用步骤](#使用步骤)
- [线损补偿（Cable Loss）](#线损补偿cable-loss)
- [掉线恢复（SIM Simulator）](#掉线恢复sim-simulator)
- [SCPI 测试流程](#scpi-测试流程)
- [项目结构](#项目结构)
- [构建与发布](#构建与发布)
- [判定标准](#判定标准)
- [注意事项](#注意事项)

---

## 功能特性

| 特性 | 说明 |
|------|------|
| 频段 | **Band 26（OB26）**，UL 814–849 MHz，Power Class 3（PMAX = 23 dBm） |
| 带宽 | 1.4 / 3 / 5 / 10 MHz，可勾选组合 |
| 信道 | 每带宽自动展开 Low / Mid / High 频点 |
| 调制 | QPSK 与 16QAM |
| RB 分配 | 每带宽按文档预设多组 (RB Size, Offset) 测试线，TBS 索引自动查表 |
| 连接方式 | 原生 **TCP Socket，端口 5025**，无需 VISA |
| 测量 | LTE Multi-Evaluation（MEV），读取 UE TX Power（已按线损补偿） |
| 线损补偿 | CMW500 **FDCorrection 修正表**，支持 CSV 模板导出 / 导入 / 自动写入激活 |
| 掉线恢复 | UE RRC 掉线时通过串口下发 **SIM Simulator AT 复位序列**自动恢复，最多 10 次 |
| 测试控制 | 开始 / 停止 / 暂停·继续，实时进度条与 SCPI 日志 |
| 报告导出 | 一键导出 `.xlsx`（内置 OpenXML 写入器，无第三方 Excel 库依赖） |
| 判定 | 传导功率 **≤ 50 dBm** 即 PASS（门限在代码 `LimitHi` 中定义） |

---

## 测试模型

程序启动即根据下表自动生成完整测试计划（可在左侧按带宽勾选裁剪）。

UL EARFCN 由公式换算：`N_UL = 26690 + 10 × (F_UL − 814)`

| 带宽 | CMW BW Code | 测试频点 (MHz) | 每频点测试线（RB @ Offset） |
|------|-------------|----------------|------------------------------|
| 1.4 MHz | `B014` | 814.7 / 819.0 / 823.3 | (1@0)(1@2)(1@5) (3@0)(3@2)(3@3) (6@0) |
| 3 MHz   | `B030` | 815.5 / 819.0 / 822.5 | (1@0)(1@7)(1@14) (8@0)(8@4)(8@7) (15@0) |
| 5 MHz   | `B050` | 816.5 / 819.0 / 821.5 | (1@0)(1@13)(1@24) (12@0)(12@6)(12@13) (25@0) |
| 10 MHz  | `B100` | 819.0 | QPSK: (1@0)(1@25)(1@49)(25@0)(25@13)(25@25)(50@0)<br>16QAM: (1@0)(1@25)(1@49)(12@0)(12@19)(12@38)(27@0) |

> 1.4 / 3 / 5 MHz 的 QPSK 与 16QAM 测试线相同；10 MHz 两种调制的 RB 分配不同。TBS 索引按「调制 + RB 数」查表得出（`QpskTbs` / `Q16Tbs`）。

---

## 运行环境

- **操作系统**：Windows 10 / 11（64-bit）
- **运行时**：发布版为自包含单文件 EXE，**无需安装 .NET 运行时**；源码构建需 [.NET 10 SDK](https://dotnet.microsoft.com/download)
- **仪表**：R&S CMW500，网络可达，SCPI 端口 5025，具备 LTE 信令 + 测量 License
- **DUT**：LTE UE（当前恢复流程针对 PLAUD SIGMA / ASR EC718 平台，通过 USB CDC / UART 串口下发 AT）

默认连接地址（可在下拉框选择）：`192.0.2.10`、`192.0.2.10`（默认选中后者）。端口固定 `5025`。

---

## 快速开始

**方式一：直接运行发布版**

从 [Releases](../../releases) 下载 `LTEFCCTesterVx.y.exe`，双击运行即可（自包含，无需安装环境）。

**方式二：源码构建运行**

```bash
cd src
dotnet restore
dotnet build -c Release
dotnet run
```

或用 Visual Studio 2022+ 打开 `src/LTEFCCTester.slnx` / `LTEFCCTester.csproj` 运行。

---

## 使用步骤

1. **选择 IP** — 左侧「连接」区选择 CMW500 的 IP，点击 **Connect**，日志栏显示 `*IDN?` 应答即连接成功。
2. **勾选带宽** — 在「测试设置」区勾选要测的带宽（默认全选），测试计划表会实时重建。
3. **（可选）配置线损** — 勾选「CMW500 线损表」，导出模板或导入自己的线损 CSV，连接后自动写入并激活。见[线损补偿](#线损补偿cable-loss)。
4. **勾选测试条目** — 在右侧「测试计划」表勾选/取消单条，或用表头复选框全选/全不选。
5. **开始测试** — 点击 **开始测试 Start**，程序逐条执行：设频/带宽 → 附着到 CEST → 下发 UDCH 分配 → TPC=MAXP 拉满功率 → MEV 测量 → 读值判定。
6. **过程控制** — 可随时 **停止** 或 **暂停/继续**（在 SCPI 步骤间即时生效）。
7. **导出报告** — 测试结束后点击 **导出 Excel**，生成带 PASS/FAIL 配色的 `.xlsx` 报告。

---

## 线损补偿（Cable Loss）

程序使用 CMW500 的 **FDCorrection（频率相关外部衰减修正表）**补偿射频线缆损耗，而非信令层标量外部衰减（标量保持 0，避免双重补偿）。

- **CSV 格式**（UTF-8 BOM）：

  ```csv
  TableName,Cable_Loss,
  ,,
  Freq(MHz),Input(dB),Output(dB)
  100,0.5,0.5
  900,0.5,0.5
  2100,1.2,1.2
  ...
  ```

  `Input` 列 → RX 路径修正表，`Output` 列 → TX 路径修正表；仪器按频率自动插值。缺 `Output` 列时沿用 `Input`。

- **导出模板**：生成示例 CSV 供填写。
- **导入线损**：解析后立即上传到 CMW500 并激活到 RF1C（若已连接；否则标记为「连接后写入」）。
- 启动时会尝试加载默认线损表 `D:\RFtest\RFtestCabloss\LossTable_Template.csv`（存在则自动载入）。
- 取消勾选会解除 CMW500 上的 RF1C 修正激活。

因此 `FETC:LTE:MEAS:MEV:MOD:AVER?` 返回的是**已补偿线损后的 UE 实际发射功率**。

---

## 掉线恢复（SIM Simulator）

测量得到无效结果（`NCAP` / 无法解析）时，说明 UE 可能掉线，程序自动触发恢复（**仅在测量失败时触发，无后台常驻轮询**）：

1. CMW500 LTE 信令 Cell OFF → ON；
2. 通过串口向 DUT 下发 SIM Simulator 私有帧 AT 复位序列：
   ```
   cd uart3_proxy
   send AT+ECSIMCFG="SimSimulator",1
   send AT+ECRST
   send AT+CFUN=0
   send AT+CFUN=1
   ```
3. 等待 ~10 s 让样机重启重附着，查询 RRC 状态；
4. 未恢复则重复，**最多 10 次**；仍失败则放弃该条目，继续下一信道。

串口自动发现（WMI 枚举 `Win32_PnPEntity`）并锁定，支持 USB CDC（3 层帧）与裸 UART（2 层帧）两种封装，波特率 230400，CRC16 校验。协议实现见 `SimSimulatorClient.cs`（逆向自随附的 `sim_simulator_tool.exe`）。

---

## SCPI 测试流程

关键指令序列（单条目 setup + measure）：

```text
# 信道设置（带宽 / EARFCN）
CONF:LTE:SIGN1:BAND OB26
CONF:LTE:SIGN1:CELL:BAND:DL <BwCode>          # 如 B050
CONF:LTE:SIGN1:CELL:BAND:UL <BwCode>
CONF:LTE:SIGN1:RFS:CHAN:UL <Earfcn>

# 附着（完整路径：Cell ON → 等待 CEST，ATT 态自动发 CONN）
CONF:LTE:SIGN1:CONN:STYP UDCH
CONF:LTE:SIGN1:CONN:UDCH:UL <Rb>,<Off>,<Mod>,<Tbs>
SOUR:LTE:SIGN1:CELL:STAT ON
# 轮询 FETC:LTE:SIGN1:PSW:STAT? 直到 CEST

# 测量
CONF:LTE:SIGN1:UL:PUSC:TPC:SET MAXP           # UE 拉满功率
CONF:LTE:MEAS:MEV:...                          # 配置 Multi-Evaluation
INIT:LTE:MEAS:MEV
# 轮询 FETC:LTE:MEAS:MEV:STAT:ALL? 直到 RDY
FETC:LTE:MEAS:MEV:MOD:AVER?                    # 解析 index[17] = TX Power(dBm)
```

结果解析：优先取返回值 **index 17**（TX Power）；若为 `NCAP`/`INV`/`NAN` 等无效标记，则回退扫描 index 14–25 取首个合理功率值（约 0–60 dBm）。

> 采用「按信道复用」策略：仅当 (带宽, EARFCN) 变化时才重新附着，同信道内只切换 UDCH 分配，缩短测试时间。

---

## 项目结构

```
LTEFCCTester/
├─ .github/workflows/release.yml   # 打 tag 自动构建单文件 EXE 并发布 Release
├─ docs/                           # 文档目录（预留）
└─ src/
   ├─ LTEFCCTester.csproj  # 项目文件（net10.0-windows, WPF, x64）
   ├─ App.xaml / App.xaml.cs       # WPF 应用入口
   ├─ MainWindow.xaml(.cs)         # 主界面 + 核心测试逻辑：
   │                               #   CmwController（TCP/SCPI）、测试计划模型、
   │                               #   测试序列、线损写入、恢复触发、xlsx 导出
   ├─ SimSimulatorClient.cs        # SIM Simulator 私有帧串口客户端（AT 复位恢复）
   ├─ sim_simulator_tool.exe       # SIM Simulator 参考工具（随发布拷贝）
   ├─ AssemblyInfo.cs / FCC.ico    # 程序集信息 / 图标
   ├─ Properties/PublishProfiles/  # 发布配置
   └─ TddMeasureSlotPatch.md       # Band 切换逻辑历史说明（早期版本）
```

> 说明：`LossSelector.cs` / `LossTable.cs` / `LossEditorWindow.cs` / `BandSwitchHandler.cs` 为早期 RFTestTool 遗留代码，已在 `.csproj` 中通过 `<Compile Remove>` 排除，不参与编译。`src/README.md` 为旧版（多频段 TPC MAX POWER）文档，已被本 README 取代。

---

## 构建与发布

**本地发布单文件 EXE：**

```bash
dotnet publish src/LTEFCCTester.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true
```

**自动发布（CI）：** 推送形如 `v1.0` / `V1.0` 的 tag，`.github/workflows/release.yml` 会在 `windows-latest` 上自动：

1. 定位唯一 `.csproj`；
2. `dotnet publish` 生成自包含单文件 EXE；
3. 按 tag 版本号重命名为 `LTEFCCTesterVx.y.exe`；
4. 创建 GitHub Release 并上传产物（自动生成 Release Notes）。

工作流通用，复制到任意 WPF 项目即可用；仓库存在多个 `.csproj` 时需在 `env.PROJECT` 指定主项目。

---

## 判定标准

| 参数 | 值 |
|------|-----|
| 频段 | Band 26（OB26） |
| UE Power Class | Class 3，PMAX = 23 dBm |
| 判定门限 | 传导功率 **≤ 50 dBm** → PASS（代码 `LimitHi`） |
| 测量项 | UE Multi-Evaluation 平均 TX Power（已补偿线损） |

导出的 `.xlsx` 报告含：信道、带宽、频率、UL EARFCN、调制、RB、Offset、实测(dBm)、Limit、Status、Message，PASS 绿 / FAIL 红配色，末尾附 `PASS/FAIL` 汇总。

---

## 注意事项

1. **端口固定 5025**，仅需选择/填写 IP。连接前确认 CMW500 网络可达且 License 已授权 LTE 信令与测量。
2. **线损与门限**：线损表通过 FDCorrection 补偿；判定门限 `LimitHi`、PMAX 等常量在 `MainWindow.xaml.cs` 顶部定义，如需调整请修改并重新构建。
3. **恢复流程针对特定 DUT**：`SimSimulatorClient` 的 AT 序列针对 PLAUD SIGMA / ASR EC718 平台，其他设备需按需修改指令序列或对接自有自动化。
4. **测试默认跳过全局初始化**（`skipInit=true`，假定样机已连接）。若需从零初始化 CMW500，可在代码中启用完整初始化分支。
5. 依赖包：`System.IO.Ports`、`System.Management`（串口发现与通信）。Excel 导出使用内置 OpenXML 写入器，无第三方 Excel 库。

---

<sub>本工具用于实验室环境下的授权射频一致性/认证摸底测试。</sub>
