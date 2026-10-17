# 输出 WAV 一致的条件（同一 VSQX，OpenUtau vs VOCALOID 编辑器）

> 目标：**同一个 .vsqx，OpenUtau 渲染出的 WAV 与 VOCALOID 编辑器一致。**
> 结论：**可行，且已实测到逐字节一致**——但前提是走 VOCALOID 引擎（plugins/vocaloid-bridge）、工程语义一致、音高不截断、渲染设置一致。走 DiffSinger / Classic 渲染器不可能一致（声源模型不同）。

---

## 1. 三个前提，逐条实测

### 前提 A：引擎是确定性的（否则“一致”无从谈起）✅

同一个渲染请求渲两次：

| 对比 | PCM SHA-256 | 差异 | 结论 |
| --- | --- | --- | --- |
| 请求 1 vs 请求 2 | 相同 | 0.0（-240 dB） | **逐字节一致** |

VOCALOID V4 引擎对同一输入是 bit-stable 的，所以“WAV 一致”是一个可达目标，不是奢望。

### 前提 B：音高模型必须是 PBS（P × S），不是固定因子 ✅

plugins/vocaloid-bridge/V4/V4ScoreWriter.cs 写明 VSQX 的两条控制曲线：**S = PBS（弯音灵敏度，半音）、P = PIT**。所以

> 音分 = P / 8192 × (S × 100)

S 是**逐音符**变化的（本工程 2–16 全都有）。用单一因子换算，无论取 0.293、0.1832 还是 1:1，都会错。错误程度直接用 WAV 量出来：

| 渲染输入 | 与参考 WAV 的差异 |
| --- | --- |
| 正确（PBS） | 参考本身 |
| 固定因子 0.1832 | **-18.1 dB**（corr 0.26） |
| OpenUtau 内置导入（未修正） | **-19.3 dB**（corr 0.44） |

### 前提 C：曲线不能被截断 ✅

OpenUtau 的 PITD 默认范围 ±1200 音分。本工程有 3.4%–4.6% 的弯音超出（例如第一个音符收尾的上滑 1377 音分）：

| 处理 | 与参考 WAV 的差异 |
| --- | --- |
| 修正曲线 + ±1200 截断 | **-45.7 dB**（corr 0.999，差 177 音分刚好是截断量） |
| 修正曲线 + 放宽到 ±3000 | **逐字节一致** |

差异点和截断点一一对应，说明残余误差**完全**来自己截断，而不是引擎或时序。

## 2. 实测阶梯（同一段 8 音符、8.25 秒）

| 路径 | null depth | 相关系数 | 判定 |
| --- | --- | --- | --- |
| 同请求渲两次 | -240 dB | 1.000 | 逐字节一致 |
| 固定因子 0.1832 | -18.1 dB | 0.262 | 不同 |
| OpenUtau 内置导入原样 | -19.3 dB | 0.440 | 不同 |
| 修正曲线、±1200 截断 | -45.7 dB | 0.9987 | 不同（截断所致） |
| **修正曲线、不截断** | **-240 dB** | **1.000** | **逐字节一致** |

复现命令：

```powershell
# 1) 参考：直接从 VSQX 生成渲染请求（PBS 模型）
.\.venv\Scripts\python.exe scripts\vsqx_render_request.py --source song.vsqx --out req-ref.json --notes 8

# 2) OpenUtau 侧：内置导入 + 修正曲线（不截断）
pwsh -NoProfile -File scripts\openutau_vsqx_import.ps1 -Source song.vsqx -Output song-native.ustx
.\.venv\Scripts\python.exe scripts\vsqx_equivalence.py apply --source song.vsqx --target song-native.ustx --out song-equal.ustx --pitch-limit-cents 3000
.\.venv\Scripts\python.exe scripts\vsqx_render_request.py --target song-equal.ustx --out req-ou.json --notes 8

# 3) 两条请求都交给 VOCALOID 引擎渲染，然后做 null test
pwsh -NoProfile -File plugins\vocaloid-bridge\tests\native_render_probe.ps1 -Config plugins\vocaloid-bridge\vocaloid4.example.json -Request req-ref.json -OutputWavPath ref.wav
pwsh -NoProfile -File plugins\vocaloid-bridge\tests\native_render_probe.ps1 -Config plugins\vocaloid-bridge\vocaloid4.example.json -Request req-ou.json -OutputWavPath ou.wav
.\.venv\Scripts\python.exe scripts\wav_compare.py ref.wav ou.wav
```

## 3. 为什么“走引擎”才能一致

引擎输出只取决于它收到的**音素序列、时序、绝对音高轨迹、声库与渲染参数**。OpenUtau 的桥接把这些重新编码成引擎自己的格式（S=24、P=delta×8192/24）；只要**绝对音高数值相同**，引擎收到的量化单元就相同，输出就逐比特相同。

这也解释了为什么不能指望 DiffSinger / Classic 路线：它们不是同一个声源模型，无法逐样本一致，只能做到“听感接近”。

## 4. 要达到一致，OpenUtau 侧还需要三项工程改动

1. **PITD 范围**：默认 ±1200 会截断真实弯音（本工程 3–5%）。要么在表达式定义里放宽，要么在导入时把超限部分转移到音符音高或分段。
2. **精度**：曲线用浮点音分传递，不要在中间取整（取整会在引擎量化边界上翻转 1 个单位，实测会把 -240 dB 拉回 -45 dB 量级）。
3. **渲染设置固化**：采样率 44.1 kHz、head 500 ms / tail 1000 ms、缓存与总线设置必须与参考一致；这些不一致同样会破坏 null test。

## 5. 边界与诚实说明

- 本结论建立在**本机 V4 引擎 + 4:1 声库 + 该 bridge 版本**上；换引擎/声库/版本需重测。
- VOCALOID5/6 的 AI 声库路径未验证（本机无 AI 声库）。
- 段落级一致（8 音符）已证明；整曲需要 tempo 变化、多轨、效果链都一致，尚未逐项验证。
- “逐字节一致”是**充分条件成立后的结果**，不是默认行为：默认路径（内置导入、默认 PITD 范围）仍有 -19 ~ -46 dB 的差异。
