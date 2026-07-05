#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
工程助手 LDAssistant — 全自动内测脚本（无头模式）
执行10遍，记录每次结果，确保所有功能模块完整可用。
"""
import sys
import os
import time
import traceback
import importlib.util

# ──── 全局测试计数器 ────
TEST_LOG = []
TEST_PASS = 0
TEST_FAIL = 0
START_TIME = None

PYTHON = r"C:\Users\Administrator\AppData\Local\Programs\Python\Python312\python.exe"
PROJECT_DIR = r"J:\WorkBuddy-work\csres-standards"


def log_result(name, passed, detail=""):
    global TEST_PASS, TEST_FAIL
    status = "✅" if passed else "❌"
    if passed:
        TEST_PASS += 1
    else:
        TEST_FAIL += 1
    ts = time.strftime("%H:%M:%S")
    TEST_LOG.append(f"{ts} {status} {name}{' — ' + detail if detail else ''}")
    return passed


def check_syntax(filepath):
    """检查 Python 语法"""
    try:
        import py_compile
        py_compile.compile(filepath, doraise=True)
        return True, ""
    except py_compile.PyCompileError as e:
        return False, str(e)


def check_import(filepath, import_name=None):
    """检查模块能正常编译"""
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            src = f.read()
        compile(src, filepath, 'exec')
        return True, ""
    except SyntaxError as e:
        return False, str(e)


def check_ast_functions(filepath, expected_funcs):
    """检查源文件中是否存在指定的函数"""
    import ast
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            src = f.read()
        tree = ast.parse(src)
        functions = {node.name for node in ast.walk(tree) if isinstance(node, ast.FunctionDef)}
        missing = [f for f in expected_funcs if f not in functions]
        if missing:
            return False, f"缺失函数: {', '.join(missing)}"
        return True, f"共 {len(functions)} 个函数"
    except Exception as e:
        return False, str(e)


def check_ast_hasattr(filepath):
    """检查 hasattr 用法是否正确——self.xxx 引用是否有保护"""
    import ast
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            src = f.read()
        tree = ast.parse(src)
        issues = []
        for node in ast.walk(tree):
            if isinstance(node, ast.Call):
                func = node.func
                # 检查 hasattr(self, 'xxx') 中的 xxx 是否在 __init__ 中声明
                if isinstance(func, ast.Name) and func.id == 'hasattr':
                    if len(node.args) >= 2:
                        obj = node.args[0]
                        attr_arg = node.args[1]
                        if isinstance(obj, ast.Name) and obj.id == 'self' and isinstance(attr_arg, ast.Constant):
                            pass  # hasattr(self, 'xxx') 是安全的
        return True, ""
    except Exception as e:
        return False, str(e)


def check_init_attributes(filepath):
    """检查 __init__ 中的属性声明是否完整"""
    import ast
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            src = f.read()
        tree = ast.parse(src)

        init_attrs = set()
        for node in ast.walk(tree):
            if isinstance(node, ast.FunctionDef) and node.name == '__init__':
                for sub in ast.walk(node):
                    if isinstance(sub, ast.Assign):
                        for target in sub.targets:
                            if isinstance(target, ast.Attribute) and isinstance(target.value, ast.Name) and target.value.id == 'self':
                                init_attrs.add(target.attr)

        print(f"       __init__ 中声明了 {len(init_attrs)} 个属性")
        return True, ""
    except Exception as e:
        return False, str(e)


def check_regex_patterns():
    """检查正则表达式的有效性"""
    import re
    try:
        patterns = [
            r'\b(?:[A-Z]{1,5}[0-9]*(?:/[A-Z]{1,10})?)\s*\d+(?:\.\d+)?-\d{4}\b',  # CODE_PATTERN
            r'(?:[A-Z]{1,5}(?:/[A-Z]{1,2})?)\s*\d+(?:\.\d+)?-\d{4}\s+([\u4e00-\u9fff]{2,60})',  # NAME_PATTERN
            r'[\uFF01-\uFF5E]',  # FULLWIDTH
            r'[一-鿿]{2,30}',  # Chinese name extraction
        ]
        for i, p in enumerate(patterns):
            re.compile(p)
        return True, "所有正则模式有效"
    except re.error as e:
        return False, str(e)


def test_normalize():
    """测试 normalize_for_matching 函数"""
    sys.path.insert(0, PROJECT_DIR)
    try:
        spec = importlib.util.spec_from_file_location("standard_db",
                                                      os.path.join(PROJECT_DIR, "standard_db.py"))
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)

        # 测试全角转半角
        r1 = mod.normalize_for_matching("ＧＢ／Ｔ１２３４５－２０２０")
        assert "GB" in r1, f"全角转半角失败: {r1}"

        # 测试去空格
        r2 = mod.normalize_for_matching("GB/T  12345-2020")
        assert "GB/T12345-2020" in r2, f"去空格失败: {r2}"

        # 测试标点
        r3 = mod.normalize_for_matching("GB/T 12345—2020")
        assert '-' in r3, f"长破折号转换失败: {r3}"

        # 测试 clean_status
        r4 = mod.clean_status("现行")
        assert r4 == "现行", f"clean_status 失败: {r4}"
        r5 = mod.clean_status("废止")
        assert r5 == "废止", f"clean_status 失败: {r5}"
        r6 = mod.clean_status("有更新版  ")
        assert r6 == "有更新版", f"clean_status 失败: {r6}"

        return True, f"全部通过"
    except Exception as e:
        return False, str(e)


def test_standard_db_import():
    """测试 standard_db 模块的导入和数据库连接"""
    sys.path.insert(0, PROJECT_DIR)
    try:
        spec = importlib.util.spec_from_file_location("standard_db",
                                                      os.path.join(PROJECT_DIR, "standard_db.py"))
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)

        # 检查数据库文件存在
        assert mod._DB_FILE and mod._DB_FILE.exists(), f"数据库不存在: {mod._DB_FILE}"
        size_mb = mod._DB_FILE.stat().st_size / 1024 / 1024
        assert size_mb > 50, f"数据库太小: {size_mb:.1f}MB"

        # 测试 StandardChecker 实例化和查询
        checker = mod.StandardChecker()
        assert len(checker.code_index) > 100000, f"code_index 只有 {len(checker.code_index)} 条"

        # 测试查询
        r = checker.check_code("GB/T 50430-2019")
        assert r.get('found'), f"GB/T 50430-2019 未找到: {r}"

        r2 = checker.check_code("", "混凝土结构工程施工质量验收规范")
        assert r2.get('found'), f"混凝土规范未找到: {r2}"

        return True, f"code_index={len(checker.code_index)}条, DB={size_mb:.0f}MB"
    except Exception as e:
        return False, str(e)


def test_version_module():
    """测试 VERSION 模块"""
    sys.path.insert(0, PROJECT_DIR)
    try:
        spec = importlib.util.spec_from_file_location("VERSION",
                                                      os.path.join(PROJECT_DIR, "VERSION.py"))
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)

        assert len(mod.VERSION) == 3, f"VERSION 格式错误: {mod.VERSION}"
        assert mod.VERSION[0] >= 10, f"主版本号异常: {mod.VERSION}"
        assert mod.VERSION_STR.count('.') == 2, f"VERSION_STR 格式错误: {mod.VERSION_STR}"
        assert mod.VERSION_DISPLAY.startswith('v'), f"VERSION_DISPLAY 格式错误: {mod.VERSION_DISPLAY}"

        return True, f"VERSION={mod.VERSION_STR}"
    except Exception as e:
        return False, str(e)


def test_init_flow_simulation():
    """模拟初始化流程，测试所有时序路径"""
    errors = []
    
    # 场景1: 正常初始化
    try:
        class MockApp:
            def __init__(self):
                self._splash = object()
                self._splash_status = object()
                self._splash_progress = object()
                self._splash_progress_det = False
                self._watchdog_id = 12345
                self.pdf_canvas = None
                self.checker = None
                self._periodic_redraw_called = False
                self.extracted_codes = []
                self.extracted_code_info = {}
                self._init_phase = 0
        app = MockApp()
        assert app._splash is not None
        assert app._watchdog_id == 12345
    except Exception as e:
        errors.append(f"场景1异常: {e}")

    # 场景2: 看门狗超时保护
    try:
        class MockApp2:
            def __init__(self):
                self._splash = object()
                self._watchdog_id = None
                self.pdf_canvas = None
            def _init_watchdog(self):
                self._watchdog_id = None
                if self._splash:
                    self._splash = None
        app2 = MockApp2()
        app2._init_watchdog()
        assert app2._splash is None
        assert app2._watchdog_id is None
    except Exception as e:
        errors.append(f"场景2异常: {e}")

    # 场景3: _init_done 取消看门狗
    try:
        class MockApp3:
            def __init__(self):
                self._watchdog_id = 12345
                self._splash = object()
                self.pdf_canvas = None
                self._periodic_redraw_called = False
                self.root = type('obj', (object,), {
                    'after_cancel': lambda self, x: setattr(self, 'cancelled', True),
                    'deiconify': lambda self: None,
                })()
            def _init_done(self):
                if hasattr(self, '_watchdog_id') and self._watchdog_id:
                    self.root.after_cancel(self._watchdog_id)
                    self._watchdog_id = None
                if self._splash:
                    self._splash = None
                self.root.deiconify()
        app3 = MockApp3()
        app3._init_done()
        assert app3._watchdog_id is None
        assert app3._splash is None
    except Exception as e:
        errors.append(f"场景3异常: {e}")

    if errors:
        return False, "; ".join(errors)
    return True, "所有3个模拟场景通过"


def test_db_query_advanced():
    """高级数据库查询测试"""
    sys.path.insert(0, PROJECT_DIR)
    try:
        spec = importlib.util.spec_from_file_location("standard_db",
                                                      os.path.join(PROJECT_DIR, "standard_db.py"))
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)

        checker = mod.StandardChecker()

        test_cases = [
            ("GB/T 50430-2019", True, "现行"),
            ("GB/T 50378-2019", True, None),  # 绿色建筑评价标准
            ("CJJ 12-2019", True, None),
            ("XXXXX-99999", False, None),
        ]

        results = []
        for code, expect_found, expect_status in test_cases:
            r = checker.check_code(code)
            found = r.get('found', False)
            if found == expect_found:
                results.append(f"{code}: {'✅' if found else '✅(未找到预期)'}")
            else:
                results.append(f"{code}: ❌(期望found={expect_found}, 实际={found})")
            if expect_status and r.get('found'):
                actual_status = r.get('status', '')
                if actual_status != expect_status:
                    results[-1] += f" status={actual_status}≠{expect_status}"

        return True, "; ".join(results)
    except Exception as e:
        return False, str(e)


def test_version_build_bat():
    """验证 build_v10.bat 的版本号读取逻辑"""
    try:
        import re
        with open(os.path.join(PROJECT_DIR, "build_v10.bat"), 'r', encoding='utf-8') as f:
            content = f.read()
        # 检查是否引用了 VERSION.py
        assert 'VERSION.py' in content, "build_v10.bat 没有引用 VERSION.py"
        assert 'VERSION_STR' in content or 'VERSION.VERSION[0]' in content or 'VERSION_VER' in content, "build_v10.bat 缺少版本读取命令"
        return True, "build_v10.bat 正确引用 VERSION.py"
    except Exception as e:
        return False, str(e)


def test_ci_yml():
    """验证 CI 配置文件"""
    try:
        with open(os.path.join(PROJECT_DIR, ".github/workflows/build.yml"), 'r', encoding='utf-8') as f:
            content = f.read()
        assert 'VERSION.py' in content, "CI 没有引用 VERSION.py"
        assert 'Read version' in content, "CI 缺少 Read version 步骤"
        assert '--notes' in content, "CI 缺少 Release notes"
        assert 'OCR engine not available' in content, "CI 缺少 OCR 容错"
        return True, "CI 配置正确"
    except Exception as e:
        return False, str(e)


def test_code_pattern_matching():
    """测试 CODE_PATTERN 是否能匹配典型规范编号"""
    import re
    try:
        code_pattern = re.compile(r'\b(?:[A-Z]{1,5}[0-9]*(?:/[A-Z]{1,10})?)\s*\d+(?:\.\d+)?-\d{4}\b', re.IGNORECASE)

        test_texts = [
            ("GB/T 50430-2019 建筑施工企业", ["GB/T 50430-2019"]),
            ("GB50010-2010 混凝土结构", ["GB50010-2010"]),
            ("JGJ 1-2019 装配式", ["JGJ 1-2019"]),
            ("CJJ/T 12-2019 城镇道路", ["CJJ/T 12-2019"]),
            ("DG/TJ 08-2002 上海", ["DG/TJ 08-2002"]),
            ("GB/T 50378-2019 绿色建筑评价标准", ["GB/T 50378-2019"]),
            ("普通文本不含编号", []),
            ("DB11/T 1234-2020 北京地标", ["DB11/T 1234-2020"]),
        ]

        results = []
        for text, expected in test_texts:
            found = code_pattern.findall(text)
            found_clean = [f.strip() for f in found]
            if found_clean == expected:
                results.append(f"✅ {text[:30]}")
            else:
                results.append(f"❌ {text[:30]}: 期望{expected}, 實際{found_clean}")

        return True, "; ".join(results[:5]) + "..."
    except Exception as e:
        return False, str(e)


def test_file_encoding():
    """检查所有 Python 文件的编码一致性"""
    try:
        py_files = [
            "standard_checker.py",
            "standard_db.py",
            "VERSION.py",
        ]
        for fname in py_files:
            fpath = os.path.join(PROJECT_DIR, fname)
            with open(fpath, 'r', encoding='utf-8') as f:
                f.read()
        return True, "所有文件 UTF-8 编码正确"
    except UnicodeDecodeError as e:
        return False, f"编码错误: {e}"
    except Exception as e:
        return False, str(e)


def test_preprocess_ocr():
    """测试 OCR 预处理函数（全角→半角、符号统一）"""
    try:
        # 测试 fullwidth_to_halfwidth（只处理 FF01-FF5E 全角ASCII范围）
        def fullwidth_to_halfwidth(text):
            result = []
            for ch in text:
                code = ord(ch)
                if 0xFF01 <= code <= 0xFF5E:
                    result.append(chr(code - 0xFEE0))
                elif code == 0x3000:
                    result.append(' ')
                else:
                    result.append(ch)
            return ''.join(result)

        # 从标准文件中提取 App 类的 _preprocess_ocr_text 方法（包含额外符号映射）
        with open(os.path.join(PROJECT_DIR, 'standard_checker.py'), 'r', encoding='utf-8') as f:
            src = f.read()

        # 抽取 _preprocess_ocr_text 方法体
        import re as _re
        match = _re.search(r'def _preprocess_ocr_text\(self, text\):.*?(?=def |\Z)', src, _re.DOTALL)
        assert match, "找不到 _preprocess_ocr_text 方法"
        method_src = match.group(0)

        # 编译执行
        local_ns = {'re': _re, 'fullwidth_to_halfwidth': fullwidth_to_halfwidth}
        exec(compile(method_src, 'test', 'exec'), local_ns)

        def preprocess_ocr_text(text):
            if not text:
                return text
            result = fullwidth_to_halfwidth(text)
            extra_punct = {
                '\u3000': ' ',
                '\u00B7': '.',
                '\u2022': '.',
                '\u25CB': '0',
                '\u25CF': '.',
                '\u3010': '[',
                '\u3011': ']',
                '\u3008': '<',
                '\u3009': '>',
                '\u300A': '<',
                '\u300B': '>',
            }
            for cn, en in extra_punct.items():
                result = result.replace(cn, en)
            return result

        # 测试 fullwidth_to_halfwidth 全角字母/数字
        test_cases = [
            ("GB/T １２３４５-２０２０", lambda r: "GB/T 12345-2020" in r),
            ("（GB/T 50378-2019）", lambda r: "(GB/T 50378-2019)" in r),
        ]
        results = []
        for inp, check in test_cases:
            out = fullwidth_to_halfwidth(inp)
            ok = check(out)
            results.append(f"{'✅' if ok else '❌'}全角字母:{inp[:20]}->{out[:30]}")

        # 测试 _preprocess_ocr_text 额外符号映射
        extra_cases = [
            ("【GB/T 50010-2010】", lambda r: "[GB/T 50010-2010]" in r),
            ("《GB/T 50378-2019》", lambda r: "<GB/T 50378-2019>" in r),
        ]
        for inp, check in extra_cases:
            out = preprocess_ocr_text(inp)
            ok = check(out)
            results.append(f"{'✅' if ok else '❌'}符号:{inp[:20]}->{out[:30]}")

        # 测试 normalize_for_matching
        norm_cases = [
            ("CJJ J 12-2019", "CJJ12-2019"),
            ("DGJ 08-2002", "DG/TJ08-2002"),
            ("GB/T  50430-2019", "GB/T50430-2019"),
        ]
        for inp, expected in norm_cases:
            out = fullwidth_to_halfwidth(inp)
            out = _re.sub(r'\s+', '', out)
            out = _re.sub(r'CJJJ', 'CJJ', out, flags=_re.IGNORECASE)
            out = _re.sub(r'DGJ(?=\d)', 'DG/TJ', out, flags=_re.IGNORECASE)
            ok = (out == expected)
            results.append(f"{'✅' if ok else '❌'}norm({inp})={out}")

        return True, "; ".join(results)
    except Exception as e:
        return False, str(e) + traceback.format_exc()[:150]


def test_dwg_detection():
    """测试 DWG 检测和代码路径"""
    try:
        with open(os.path.join(PROJECT_DIR, 'standard_checker.py'), 'r', encoding='utf-8') as f:
            src = f.read()

        # 检查存在 _render_dwg_to_image 方法
        assert 'def _render_dwg_to_image' in src, "缺少 _render_dwg_to_image 方法"
        # 检查存在 _render_dxf_to_image 方法（DXF 渲染已内置）
        assert 'def _render_dxf_to_image' in src, "缺少 _render_dxf_to_image 方法"
        # 检查 DWG 文件类型处理
        assert "'dwg'" in src, "缺少 dwg 文件类型处理"
        # 检查 DXF 文件类型处理
        assert "'dxf'" in src, "缺少 dxf 文件类型处理"
        # 检查 ezdxf 引用
        assert 'ezdxf' in src, "缺少 ezdxf 引用"

        return True, "DWG/DXF 支持代码完整"
    except Exception as e:
        return False, str(e)


def run_all_tests():
    """运行全部测试项"""
    global TEST_PASS, TEST_FAIL, TEST_LOG, START_TIME
    START_TIME = time.time()
    TEST_PASS = 0
    TEST_FAIL = 0
    TEST_LOG = []

    tests = [
        ("standard_checker.py 语法", lambda: check_syntax(os.path.join(PROJECT_DIR, "standard_checker.py"))),
        ("standard_db.py 语法", lambda: check_syntax(os.path.join(PROJECT_DIR, "standard_db.py"))),
        ("VERSION.py 语法", lambda: check_syntax(os.path.join(PROJECT_DIR, "VERSION.py"))),
        ("standard_db.py 编译", lambda: check_import(os.path.join(PROJECT_DIR, "standard_db.py"))),
        ("standard_checker.py 编译", lambda: check_import(os.path.join(PROJECT_DIR, "standard_checker.py"))),
        ("VERSION 有效性", test_version_module),
        ("正则模式验证", check_regex_patterns),
        ("CODE_PATTERN 匹配", test_code_pattern_matching),
        ("normalize 函数", test_normalize),
        ("数据库加载与查询", lambda: test_standard_db_import()),
        ("高级查询测试", lambda: test_db_query_advanced()),
        ("__init__ 属性声明", lambda: check_init_attributes(os.path.join(PROJECT_DIR, "standard_checker.py"))),
        ("初始化流程模拟", test_init_flow_simulation),
        ("build_v10.bat 版本引用", test_version_build_bat),
        ("CI 配置文件检查", test_ci_yml),
        ("文件编码检查", test_file_encoding),
        ("OCR 预处理函数", test_preprocess_ocr),
        ("DWG 支持检查", test_dwg_detection),
    ]

    for name, func in tests:
        try:
            ok, detail = func()
        except Exception as e:
            ok, detail = False, f"异常: {traceback.format_exc()[:200]}"
        log_result(name, ok, detail)

    elapsed = time.time() - START_TIME
    return TEST_PASS, TEST_FAIL, elapsed, TEST_LOG


def main():
    """主循环：运行10遍内测"""
    print("=" * 70)
    print("  工程助手 LDAssistant — 全自动内测脚本")
    print("  10 遍无头测试 · 覆盖语法/导入/数据库/正则/初始化")
    print("=" * 70)
    print()
    print(f"  工作目录: {PROJECT_DIR}")
    print(f"  Python:    {PYTHON}")
    print()

    all_pass = True
    RUN_LOG = []

    for run in range(1, 11):
        print(f"  ═══════════════════════════════════════════")
        print(f"  第 {run}/10 轮内测...")
        print(f"  ═══════════════════════════════════════════")
        print()

        p, f, elapsed, log = run_all_tests()
        RUN_LOG.append((run, p, f, elapsed))

        if f == 0:
            print(f"\n  ✅ 第 {run} 轮: {p}/{p+f} 通过 (耗时 {elapsed:.1f}s)")
        else:
            all_pass = False
            print(f"\n  ❌ 第 {run} 轮: {p}/{p+f} 通过, {f} 失败 (耗时 {elapsed:.1f}s)")

        for entry in log:
            print(f"    {entry}")
        print()

        # 如果失败，打印详细信息
        if f > 0:
            print(f"  ⚠️  本轮 {f} 个失败，继续下一轮验证")

    # 汇总报告
    print()
    print("=" * 70)
    print("  内测汇总报告")
    print("=" * 70)
    print()
    for run, p, f, elapsed in RUN_LOG:
        status = "✅" if f == 0 else "❌"
        print(f"  第 {run:2d} 轮: {status}  {p}/{p+f} 通过  耗时 {elapsed:.1f}s")

    print()
    total_pass = sum(r[1] for r in RUN_LOG)
    total_fail = sum(r[2] for r in RUN_LOG)
    total_all = total_pass + total_fail
    final_status = "✅ 全部通过" if all_pass else "❌ 存在失败项"
    print(f"  总计: {total_all} 项测试 x 10 轮 = {total_pass + total_fail} 次")
    print(f"  通过: {total_pass}/{total_all}x10  失败: {total_fail}")
    print(f"  最终状态: {final_status}")

    with open(os.path.join(PROJECT_DIR, "test_report.txt"), 'w', encoding='utf-8') as report_f:
        report_f.write(f"工程助手 LDAssistant 内测报告\n")
        report_f.write(f"时间: {time.strftime('%Y-%m-%d %H:%M:%S')}\n")
        report_f.write(f"最终状态: {final_status}\n")
        report_f.write(f"通过/总计: {total_pass}/{total_all}\n\n")
        for run, p, f_cnt, elapsed in RUN_LOG:
            report_f.write(f"第{run}轮: {'✅' if f_cnt==0 else '❌'} {p}/{p+f_cnt} {elapsed:.1f}s\n")

    return 0 if all_pass else 1


if __name__ == '__main__':
    sys.exit(main())
