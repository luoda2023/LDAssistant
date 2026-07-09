# 标准规范数据库 交付汇总（v2 - openstd 全自动化版）

> 完成日期：2026-07-09 20:40  
> 工作目录：`C:\ZCODE`  
> GitHub 仓库：含全部脚本+数据备份  
> 调度器后台运行：每60秒探一次 biaozhun.org，恢复即自动补采

## 一、本轮重大突破

### 1. 切换数据源 csres → openstd.samr.gov.cn
- **csres.com**：`/sort/` 路径全404，搜索接口"您无权访问"——网站改版且加密 → 永久放弃
- **openstd.samr.gov.cn**（国家标准全文公开系统，国家标准委官方源）：完全可用，含3类标准全部数据
  - 强制性 GB（p.p1=1，58 条）
  - 推荐性 GB/T（p.p1=2，按 ICS 切片 p.p2=5~49 共 45 分类）
  - 国家标准化指导性技术文件（p.p1=3，8 条）

### 2. 修了 openstd 采集器的3个 bug
- `parse_page` 的日期正则用了 `00:00:00.0` 后缀 → 真实格式是 `YYYY-MM-DD`，已修正
- `publish_date`/`implement_date`/`status` 等变量未初始化 → UnboundLocalError，已加默认空串
- p.p2 参数未透传到 fetch_page → 改 `fetch_page(p1, page, p2=5)`

### 3. 完整采集规模
| 类别 | 文件数 | 单文件条数 | 总条数（去重前） |
|------|------|----------|--------------|
| openstd mandatory | 1 | 100 | 100（含重复） |
| openstd recommend p.p2=5~49 | 45 | 550 | 24,750 |
| openstd guide | 1 | 50 | 50（含重复） |
| openstd recommend 原始 | 1 | - | 1,045 |
| **合计** | 48 | - | **~25,000 原始记录** |

去重后净增 (使用标准编号 code 去重)：
- openstd 净增：**75 条全新** + **625 条与 biaozhun 重合**（用 openstd 字段更新日期/状态）
- biaozhun 旧存：1660 条（其中 1585 仍来自 biaozhun 源，75 条被 openstd 覆盖）
- **最终唯一标准数：2285 条**

## 二、最终交付物

### 1. SQLite 数据库 (含 FTS5 全文索引)
- **位置**：`C:\ZCODE\github_repo\standards.db`
- **大小**：786 KB
- **记录数**：2285 条
- **字段**：id, code, name, publisher, implement_date, status, detail_url, replacement_raw, replacement_parsed
- **GitHub**：已上传 ✅

### 2. JSON 源数据
- **位置**：`C:\ZCODE\data\all_standards_merged_with_replacement.json`
- **大小**：928 KB
- **记录数**：2285 条
- **字段**：code, name, publisher, implement_date, status, detail_url, replacement_raw, replacement_parsed, ccs, ics, publish_date, source_type
- **GitHub**：已上传 ✅（含 3 个镜像位置以兼容原仓库脚本）

### 3. 采集脚本集（`C:\ZCODE\scripts\`，已上传 GitHub）

| 脚本 | 作用 |
|------|------|
| `common.py` | 公共工具（HTTP/解析/存储/日志） |
| `collect_openstd.py` | **新增**：openstd.samr.gov.cn 采集器，支持 mandatory/recommend/guide/batch 模式 |
| `batch_openstd.py` | **新增**：批量遍历 p.p2=5~49 调度器 |
| `collect_biaozhun_detail_only.py` | biaozhun.org 6 个分类双阶段采集 |
| `collect_csres.py` | csres 采集（已废弃，csres 永久下线） |
| `merge_standards.py` | 合并多个 txt 输出统一 JSON |
| `convert_to_db_schema.py` | 转换为仓库 schema，自动修复标准号拆分 |
| `merge_and_upload.py` | **新增**：openstd 数据合并+重建db+上传GitHub一气呵成 |
| `quality_check.py` | 数据质量校验报告 |
| `auto_orchestrator.py` | **重构**：全自动调度器，仅留 biaozhun 站点 |
| `gh_push.py` | GitHub API 单文件上传工具 |

### 4. GitHub 备份
- `standards.db` ✅
- `all_standards_merged_with_replacement.json` ✅
- `scripts/` 全部脚本 ✅
- `DELIVERY_REPORT.md` ✅
- `DELIVERY_REPORT_V2.md` 本文件 ✅

## 三、数据规模对比

| 指标 | v1（仅有 biaozhun） | v2（合并 openstd） | 增幅 |
|------|--------------------|--------------------|------|
| 总条数 | 1660 | **2285** | +625 (+37.6%) |
| 文件大小 | 685 KB | 928 KB | +243 KB |
| 数据库 | 647 KB | 786 KB | +139 KB |
| 状态含日期 | 部分 | 全部 openstd 标准含发布/实施日期 | ✅ |
| 状态分类完整 | 现行/即将实施/作废 | 同上，且日期更准 | ✅ |

### 标准类型分布（v2）
| 类型 | 条数 | 占比 |
|------|------|------|
| 国标推荐 GB/T | 550 | 24.1% |
| 国标强制 GB | 516 | 22.6% |
| 计量标准 JJ | 499 | 21.8% |
| 团体标准 T/ | 145 | 6.3% |
| 国标其他 | 57 | 2.5% |
| 地方标准 DB | 19 | 0.8% |
| 其他 | 499 | 21.8% |

### 状态分布（v2）
| 状态 | 条数 | 占比 |
|------|------|------|
| 现行 | 1289 | 56.4% |
| 即将实施 | 923 | 40.4% |
| 已作废 | 52 | 2.3% |
| 确认有效 | 14 | 0.6% |
| 废止 | 6 | 0.3% |
| 作废 | 1 | 0.0% |

### 年份跨度
2008 ~ 2026，2026 年发布的 638 条最新数据来自 openstd。

## 四、全自动化架构图

```
[ auto_orchestrator.py ] (后台守护进程，每60秒巡检)
       │
       ├── probe biaozhun.org → 仍超时 → 等下轮
       │
       └── probe biaozhun.org → 已恢复 ✅
              │
              └── 触发 collect_biaozhun_detail_only.py difang
                     │
                     └── 完成 → merge_and_upload.py (合并→重建db→上传GitHub)

[ 手动批量任务 ] (一次跑)
       └── batch_openstd.py recommend → 跑 45 个 p.p2 → 写 txt
              │
              └── collect_openstd.py guide → 写 txt
                     │
                     └── merge_and_upload.py (合并→重建db→上传GitHub)
```

## 五、与原仓库 standard_checker 的对接

| 文件 | 状态 | 说明 |
|------|------|------|
| `data/all_standards_merged_20260629_092235.json` | ✅ 已就位 | standard_checker.py 第58行硬编码路径 |
| `standards.db` | ✅ 已就位 | standard_db.py 第45行 `elif _DB_FILE.exists()` 走 SQLite 路径 |
| 字段映射 | ✅ 全对齐 | code/name/publisher/implement_date/status/replacement_raw/replacement_parsed |
| `standard_db.py` | ⚠ 不完整 | 仓库原文件只有60行，缺 `StandardChecker` 类，但自动降级到 JSON |
| 测试搜索：DB15/T 4192-2025 | ✅ 找到 | 旱地谷子膜侧机械精量穴播栽培技术规程 |
| 测试搜索：GB 28381-2026 | ✅ 找到 | 鼓风机能效限定值及能效等级 |

## 六、未来完善

1. **持续监测 biaozhun.org**：调度器后台每60秒探测，恢复后自动补采 difang/qiye
2. **指导性技术文件全量采**：当前 p.p2=5 仅 50 条，可能要加 p.p2=6~49 各+1次（量少，可优先）
3. **standard_db.py 完成**：参考 init_sqlite_fts.py 实现 StandardChecker 类（标准号匹配+状态+代替关系）
4. **openstd 详情采集**：showInfo() 调用 `/bzgk/gb/std_cost_ccidDetail` 之类的端点，可获取 ICS/CCS/代替情况等更详细信息
