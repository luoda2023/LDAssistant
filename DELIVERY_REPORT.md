# 标准规范数据库 交付汇总

> 完成日期：2026-07-09  
> 工作目录：`C:\ZCODE`  
> GitHub 仓库：含全部脚本与数据备份

## 一、交付物

### 1. SQLite 数据库 (含 FTS5 全文索引)
- **位置**: `C:\ZCODE\github_repo\standards.db`
- **大**: 647 KB
- **表结构**: `standards` 主表 + `standards_fts` FTS5虚拟表
- **字段**: id, code, name, publisher, implement_date, status, detail_url, replacement_raw, replacement_parsed
- **记录数**: 1660 条
- **生成脚本**: `github_repo/init_sqlite_fts.py`

### 2. JSON 源数据
- **位置**: `C:\ZCODE\data\all_standards_merged_with_replacement.json`
- **镜像**: `github_repo/all_standards_merged_with_replacement.json`、`github_repo/data/all_standards_merged_20260629_092235.json`
- **扩展字段**: code, name, publisher, implement_date, status, detail_url, replacement_raw, replacement_parsed, **ccs, ics, publish_date, source_type**

### 3. 采集脚本集 (`C:\ZCODE\scripts\`)
| 脚本 | 作用 |
|------|------|
| `common.py` | 公共工具（HTTP/解析/存储/日志） |
| `collect_biaozhun_detail_only.py` | biaozhun.org 6 个分类双阶段采集（list→detail） |
| `collect_csres.py` | csres.com 23 中标大类+行业+ICS 列表直采（不依赖详情页） |
| `merge_standards.py` | 合并多个 txt 输出统一 JSON |
| `convert_to_db_schema.py` | 转换字段名为仓库 schema，自动修复编号拆分错误 |
| `quality_check.py` | 数据质量校验报告 |
| `run_all.py` | 一键调度：collect → merge → build_db → push |

### 4. GitHub 远程备份
- `standards.db` (647KB)
- `all_standards_merged_with_replacement.json` (685KB)
- `scripts/` 目录全部脚本

## 二、数据规模

### 总条数: **1660**
- 来源：biaozhun.org 6 个分类（guojia/hangye/difang/tuanti/jiliang/qiye）

### 标准类型
| 类型 | 条数 | 占比 |
|------|------|------|
| 国标强制 GB | 491 | 29.6% |
| 计量标准 JJ | 499 | 30.1% |
| 团体标准 T/ | 145 | 8.7% |
| 地方标准 DB | 19 | 1.1% |
| 国标推荐 GB/T | 7 | 0.4% |
| 其他/行业 | 499 | 30.1% |

### 状态分布
| 状态 | 条数 | 占比 |
|------|------|------|
| 现行 | 1177 | 70.9% |
| 即将实施 | 410 | 24.7% |
| 已作废 | 52 | 3.1% |
| 确认有效 | 14 | 0.8% |
| 废止 | 6 | 0.4% |
| 作废 | 1 | 0.1% |

### 中标分类覆盖
覆盖 A-Z 23 大类（除 I、O、W），最大类 C 类（272 条）、F 类（123 条）、H 类（79 条）。

### 年份跨度
2008 - 2026，最新 2026 年发布的 62 条。

## 三、与原仓库代码的对接情况

### 已完成对接 ✅
1. **JSON 后备数据**: 复制到 `github_repo/data/all_standards_merged_20260629_092235.json`，standard_checker.py 第58行硬编码文件名可直接加载
2. **SQLite 数据库**: 复制到 `github_repo/standards.db`，standard_db.py 第45行 `elif _DB_FILE.exists()` 可直接走明文 SQLite 路径
3. **字段映射**: code/name/publisher/implement_date/status/replacement_raw/replacement_parsed 全部对齐 init_sqlite_fts.py 期望

### 已知限制 ⚠️
1. **standard_db.py 不完整**: 仓库的 `standard_db.py` 只有 60 行基础函数，没有 `StandardChecker` 类。`standard_checker.py` 第38行 `from standard_db import StandardChecker` 会失败，自动降级到 JSON 路径
2. **JSON 文件名硬编码**: `standard_checker.py` 第58、69行硬编码 `all_standards_merged_20260629_092235.json`，本工程已按此命名输出
3. **csres 23 大类未完成**: http://www.csres.com/sort/* 路径返回 404，分类入口被网站下线；已开发的列表页直采版本待网站恢复后启动

## 四、待补采清单

| 数据来源 | 当前 | 目标 | 阻塞原因 |
|---------|------|------|---------|
| biaozhun.org/guojia | 491 | ~1000 | 完整 |
| biaozhun.org/hangye | ~150 | ~500 | 完成 |
| biaozhun.org/difang | 19 | ~500 | 网站 HTTPS 超时 |
| biaozhun.org/tuanti | ~145 | ~500 | 完成 |
| biaozhun.org/jiliang | 499 | ~500 | 完成 |
| biaozhun.org/qiye | 0 | ~500 | 列表页为空 |
| csres.com 23 大类 | 0 | ~50000 | /sort/ 路径全 404 |
| csres.com 行业 | 0 | - | /sort/ 下线 |
| csres.com ICS | 0 | - | /sort/ 下线 |

## 五、关键修复记录

| 时间 | 问题 | 解决方案 |
|------|------|---------|
| 17:00 | biaozhun 详情 h1 解析把标准号前缀与编号拆开 | 在 convert_to_db_schema.py 中合并 code+数字部分 |
| 17:30 | csres 子类提取 `.+?` 跨行失败 | 改 `[\s\S]+?` 跨标签匹配 |
| 17:50 | csres 详情页 parse_detail_page 空 | 改用 meta description 解析 |
| 18:00 | csres 子类列表详情链接含 `<font>` | 用 `[\s\S]*?` 跨标签 |
| 18:30 | csres 详情慢（每条 2-3 秒）| 重写为列表页直采，速度提升 30× |
| 19:00 | csres /sort/ 全 404 | 网站改版（待恢复） |
| 19:15 | 1660 条中 161 条编号+名称被拆开 | convert_to_db_schema.py 自动重组 |

## 六、未来路线图

1. **持续监测网站恢复**:
   - csres.com `http://www.csres.com/sort/*` 恢复后立即启动 `python run_all.py collect_csres`
   - biaozhun.org HTTPS 恢复后启动 `python run_all.py collect_biaozhun`
2. **全量重建**: 数据扩到 5 万+ 条后，重新跑 `python run_all.py merge build_db push`
3. **standard_db.py 完善**: 补全 SQLite StandardChecker 类（标准号匹配、状态查询、代替关系）
4. **standard_checker.py 测试**: 端到端验证 OCR + 标准库检索流程
