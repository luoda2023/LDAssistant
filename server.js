/**
 * 工程建设标准数据库 - Web查询服务 v2
 * 
 * 功能：
 * - AI 对话查询（LLM API + 标准搜索工具调用）
 * - 标准搜索 API（标准号/名称）
 * - 多渠道数据源（standards_gov_full.db）
 * - 统计看板、分类列表
 */

const express = require('express');
const initSqlJs = require('sql.js');
const fs = require('fs');
const path = require('path');
const http = require('http');

// ======= 配置 =======
const DB_PATH = path.join(__dirname, 'standards_gov_full.db');
const PORT = process.env.PORT || 3000;
const LLM_API_KEY = process.env.LLM_API_KEY || '';
const LLM_BASE_URL = process.env.LLM_BASE_URL || 'https://api.chatnio.net';
const LLM_MODEL = process.env.LLM_MODEL || 'glm-5.1';

const app = express();
app.use(express.json({ limit: '10mb' }));
app.use(express.static(path.join(__dirname, 'public')));

// Express 5 + async 路由错误处理包装器
function asyncHandler(fn) {
  return (req, res, next) => {
    Promise.resolve(fn(req, res, next)).catch(next);
  };
}

// ======= CORS =======
app.use((req, res, next) => {
  res.header('Access-Control-Allow-Origin', '*');
  res.header('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
  res.header('Access-Control-Allow-Headers', 'Content-Type');
  if (req.method === 'OPTIONS') return res.sendStatus(200);
  next();
});

// ======= 数据库连接 =======
let db = null;
let SQL = null;

async function getDb() {
  if (db) return db;
  SQL = await initSqlJs();
  const buf = fs.readFileSync(DB_PATH);
  db = new SQL.Database(buf);
  // 检查字段
  const cols = db.exec("PRAGMA table_info(standards)");
  const colNames = cols[0]?.values.map(v => v[1]) || [];
  console.log('数据库字段:', colNames.join(', '));
  console.log('总条数:', db.exec('SELECT COUNT(*) FROM standards')[0]?.values[0]?.[0]);
  return db;
}

// ======= AI 对话 =======

/** 调用 LLM API */
async function callLLM(messages, tools, baseUrl, apiKey, model) {
  const effectiveBaseUrl = baseUrl || LLM_BASE_URL;
  const effectiveApiKey = apiKey || LLM_API_KEY;
  const effectiveModel = model || LLM_MODEL;
  
  const body = {
    model: effectiveModel,
    messages,
    stream: false,
    temperature: 0.3
  };
  if (tools && tools.length > 0) body.tools = tools;
  
  return new Promise((resolve, reject) => {
    const postData = JSON.stringify(body);
    const url = new URL(effectiveBaseUrl + '/v1/chat/completions');
    const isHttps = url.protocol === 'https:';
    const httpMod = isHttps ? require('https') : require('http');
    
    const req = httpMod.request({
      hostname: url.hostname,
      port: url.port || (isHttps ? 443 : 80),
      path: url.pathname,
      method: 'POST',
      rejectUnauthorized: false,
      headers: {
        'Content-Type': 'application/json',
        'Authorization': 'Bearer ' + effectiveApiKey,
        'Content-Length': Buffer.byteLength(postData)
      }
    }, res => {
      let data = '';
      res.on('data', chunk => data += chunk);
      res.on('end', () => {
        try {
          const parsed = JSON.parse(data);
          resolve(parsed);
        } catch (e) {
          reject(new Error('LLM响应解析失败: ' + data.substring(0, 200)));
        }
      });
    });
    
    req.on('error', reject);
    req.setTimeout(60000, () => { req.destroy(); reject(new Error('LLM请求超时')); });
    req.write(postData);
    req.end();
  });
}

/** 标准搜索工具 */
const searchTool = {
  type: 'function',
  function: {
    name: 'search_standards',
    description: '搜索国家标准数据库，支持按标准号、关键词查找标准',
    parameters: {
      type: 'object',
      properties: {
        q: { type: 'string', description: '搜索关键词（标准号或标准名称，如"GB 50068"或"混凝土结构"）' },
        type: { type: 'string', description: '标准类型筛选，如 GB, GB/T, JGJ, CJJ, DB' },
        status: { type: 'string', description: '标准状态，如 现行, 废止, 即将实施' },
        year: { type: 'string', description: '发布年份，如 2024' },
        from: { type: 'string', description: '起始年份，如 2020' },
        to: { type: 'string', description: '结束年份，如 2024' },
        page: { type: 'number', description: '页码，从1开始', default: 1 },
        pageSize: { type: 'number', description: '每页条数', default: 20 }
      },
      required: ['q']
    }
  }
};

/** 执行搜索（使用sql.js） */
function execSearch(params) {
  const db = getDbSync();
  let where = [];
  let bindParams = [];
  
  if (params.q) {
    const q = params.q.trim();
    const hasChinese = /[\u4e00-\u9fff]/.test(q);
    if (/[A-Za-z0-9/-]/.test(q) && !hasChinese) {
      where.push('(code LIKE ? OR name LIKE ?)');
      bindParams.push('%' + q.replace(/\s+/g, '') + '%', '%' + q + '%');
    } else {
      const terms = q.split(/\s+/).filter(Boolean);
      const conds = terms.map(t => {
        bindParams.push('%' + t + '%', '%' + t + '%');
        return '(name LIKE ? OR code LIKE ?)';
      });
      where.push('(' + conds.join(' AND ') + ')');
    }
  }
  
  if (params.type) {
    const types = params.type.split(',');
    where.push('source_type IN (' + types.map(() => '?').join(',') + ')');
    bindParams.push(...types);
  }
  if (params.status) {
    where.push('status = ?');
    bindParams.push(params.status);
  }
  if (params.year) {
    where.push("(publishDate LIKE ? OR publishDate GLOB ?)");
    bindParams.push(params.year + '%', params.year + '-??-??');
  }
  if (params.from) {
    where.push('publishDate >= ?');
    bindParams.push(params.from + '-01-01');
  }
  if (params.to) {
    where.push('publishDate <= ?');
    bindParams.push(params.to + '-12-31');
  }
  
  const whereClause = where.length ? 'WHERE ' + where.join(' AND ') : '';
  const pg = Math.max(1, parseInt(params.page) || 1);
  const ps = Math.min(100, Math.max(1, parseInt(params.pageSize) || 20));
  const offset = (pg - 1) * ps;
  
  try {
    const countResult = db.exec('SELECT COUNT(*) FROM standards s ' + whereClause, bindParams);
    const total = countResult?.[0]?.values?.[0]?.[0] || 0;
    
    const dataResult = db.exec(`
      SELECT s.code, s.name, s.enName, s.status, s.ics, s.ccs, s.publishDate, s.implementDate, s.department, s.manager, s.issuer, s.source_type
      FROM standards s ${whereClause}
      ORDER BY LENGTH(s.code), s.code
      LIMIT ? OFFSET ?
    `, [...bindParams, ps, offset]);
    
    const rows = (dataResult?.[0]?.values || []).map(row => ({
      code: row[0], name: row[1], enName: row[2],
      status: row[3], ics: row[4], ccs: row[5],
      publishDate: row[6], implementDate: row[7],
      department: row[8], manager: row[9], issuer: row[10],
      source: row[11]
    }));
    
    return { total, page: pg, pageSize: ps, totalPages: Math.ceil(total / ps), data: rows };
  } catch(e) {
    console.error('execSearch错误:', e.message);
    return { total: 0, page: pg, pageSize: ps, totalPages: 0, data: [], error: e.message };
  }
}

/** 同步获取数据库（用于AI工具调用） */
function getDbSync() {
  if (!db) throw new Error('数据库未初始化');
  return db;
}

// ======= API 路由 =======

/** API代理（用于前端测试连接和百度搜索） */
app.post('/api/proxy', (req, res) => {
  const doProxy = async () => {
    try {
      const { url, apiKey, body, method } = req.body;
      if (!url) return res.status(400).json({ error: 'URL不能为空' });
      
      const isBaidu = url.includes('baidu.com') || url.includes('baidu');
      const isLLM = url.includes('/v1/chat/completions');
      
      if (isBaidu) {
        // 百度搜索 - 用服务器抓取
        const https = require('https');
        const http = require('http');
        
        try {
          const html = await new Promise((resolve, reject) => {
            const urlObj = new URL(url);
            const mod = urlObj.protocol === 'https:' ? https : http;
            mod.get(url, {
              headers: {
                'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
                'Accept': 'text/html,application/xhtml+xml',
                'Accept-Language': 'zh-CN,zh;q=0.9'
              }
            }, res2 => {
              let d = '';
              res2.on('data', c => d += c);
              res2.on('end', () => resolve(d));
            }).on('error', reject);
          });
          
          const results = [];
          const blocks = html.split('<div class="result');
          blocks.forEach(block => {
            const titleMatch = block.match(/<h3[^>]*>([\s\S]*?)<\/h3>/);
            const linkMatch = block.match(/href="(https?:\/\/[^"]+)"/);
            if (titleMatch) {
              results.push({
                title: titleMatch[1].replace(/<[^>]+>/g, '').trim(),
                link: linkMatch ? linkMatch[1] : '',
                snippet: ''
              });
            }
          });
          return res.json({ success: true, results: results.slice(0, 10), total: results.length });
        } catch(e) {
          return res.json({ success: false, error: e.message });
        }
      }
      
      if (isLLM) {
        // LLM调用
        const https = require('https');
        const http = require('http');
        const urlObj = new URL(url);
        const mod = urlObj.protocol === 'https:' ? https : http;
        const postData = JSON.stringify(body);
        
        const data = await new Promise((resolve, reject) => {
          const req = mod.request({
            hostname: urlObj.hostname,
            port: urlObj.port || (urlObj.protocol === 'https:' ? 443 : 80),
            path: urlObj.pathname,
            method: 'POST',
            rejectUnauthorized: false,
            headers: {
              'Content-Type': 'application/json',
              'Authorization': 'Bearer ' + apiKey,
              'Content-Length': Buffer.byteLength(postData)
            }
          }, res2 => {
            let d = '';
            res2.on('data', c => d += c);
            res2.on('end', () => { try { resolve(JSON.parse(d)); } catch(e) { resolve({ error: 'parse error' }); } });
          });
          req.on('error', reject);
          req.write(postData);
          req.end();
        });
        return res.json(data);
      }
      
      // 通用请求
      const response = await fetch(url, {
        method: method || 'POST',
        headers: { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + apiKey },
        body: body ? JSON.stringify(body) : undefined
      });
      const data = await response.json();
      res.json(data);
    } catch (e) {
      res.status(500).json({ error: e.message });
    }
  };
  doProxy();
});

/** 1. AI 对话 */
app.post('/api/chat', asyncHandler(async (req, res) => {
  try {
    const { message, history, conversationId, apiConfig } = req.body;
    if (!message) return res.status(400).json({ error: '消息不能为空' });
    
    await getDb(); // 确保数据库已加载
    
    // 使用前端传来的API配置，或回退到环境变量
    const effectiveConfig = apiConfig || {};
    const currentBaseUrl = effectiveConfig.baseUrl || LLM_BASE_URL;
    const currentApiKey = effectiveConfig.apiKey || LLM_API_KEY;
    const currentModel = effectiveConfig.model || LLM_MODEL;
    
    // 如果没有配置API Key，返回配置引导
    if (!currentApiKey) {
      return res.json({
        reply: '⚠️ **请先配置 API**\n\n点击右上角 **⚙️ 配置** 按钮，设置你的 API 地址和 Key。\n\n支持 OpenAI 兼容格式的任意 API 服务，包括：\n- **chatnio** (api.chatnio.net)\n- **DeepSeek** (api.deepseek.com)\n- **OpenAI** (api.openai.com)\n- 以及其他兼容接口',
        searchResults: []
      });
    }
    
    // 构建消息列表
    const systemPrompt = `你是标准查询AI助手，帮助用户查找和分析中国国家标准、行业标准、地方标准。

你拥有以下能力：
1. 搜索标准库：当用户需要查标准时，使用 search_standards 工具
2. 解释标准内容：根据搜索结果回答用户问题
3. 推荐相关标准：根据用户需求推荐相关标准

注意事项：
- 搜索结果要简洁清晰，列出标准号、名称、状态
- 如果搜索到的标准很多，告诉用户总数并展示前几条
- 用户可能问标准号（如"GB 50068"）或关键词（如"混凝土规范"）
- 标准号中"/"是标准的一部分，不要省略
- 当前数据库共有43万+条标准记录，包括国标(GB/GB/T)、行标(JGJ/CJJ等)、地标(DB)等

当前时间：${new Date().toISOString().split('T')[0]}`;
    
    const messages = [
      { role: 'system', content: systemPrompt },
      ...(history || []),
      { role: 'user', content: message }
    ];
    
    // 第一次调用，带tool（使用前端配置）
    const response = await callLLM(messages, [searchTool], currentBaseUrl, currentApiKey, currentModel);
    
    console.log('LLM响应:', JSON.stringify(response).substring(0, 500));
    
    const choice = response.choices?.[0];
    if (!choice) {
      return res.json({ reply: '抱歉，AI 暂时无法回答，请稍后重试。', debug: response?.error || response?.message || '未知错误' });
    }
    
    // 检查是否需要调用工具
    if (choice.finish_reason === 'tool_calls' && choice.message?.tool_calls) {
      const toolCall = choice.message.tool_calls[0];
      const args = JSON.parse(toolCall.function.arguments);
      
      // 执行搜索
      const searchResult = execSearch(args);
      
      // 把搜索结果和工具调用结果发给LLM生成最终回答
      messages.push(choice.message);
      messages.push({
        role: 'tool',
        tool_call_id: toolCall.id,
        content: JSON.stringify(searchResult)
      });
      
      // 第二次调用，生成回答（不再给工具）
      const finalResponse = await callLLM(messages, [searchTool], currentBaseUrl, currentApiKey, currentModel);
      const finalChoice = finalResponse.choices?.[0];
      
      let reply = finalChoice?.message?.content || '搜索完成，但未能生成回答。';
      
      // 格式化回复中的搜索结果
      let resultHTML = reply;
      
      // 如果搜索到了结果，展示搜索卡片
      if (searchResult.data && searchResult.data.length > 0) {
        res.json({ 
          reply: resultHTML,
          searchResults: searchResult.data.slice(0, 10).map(r => ({
            id: r.code,
            code: r.code,
            name: r.name,
            status: r.status,
            publishDate: r.publishDate,
            ics: r.ics
          })),
          totalResults: searchResult.total
        });
      } else {
        res.json({ reply: resultHTML, searchResults: [] });
      }
    } else {
      // 直接回答
      const reply = choice.message?.content || '抱歉，我没有理解您的问题。';
      res.json({ reply, searchResults: [] });
    }
  } catch (e) {
    console.error('Chat error:', e);
    res.status(500).json({ reply: '⚠️ 系统繁忙，请稍后重试。错误: ' + e.message, searchResults: [] });
  }
}));

/** 2. 搜索标准 */
app.get('/api/search', asyncHandler(async (req, res) => {
  try {
    await getDb();
    const result = execSearch(req.query);
    res.json(result);
  } catch (e) {
    res.status(500).json({ error: e.message });
  }
}));

/** 3. 标准详情（支持模糊匹配） */
app.get('/api/standard/:code', asyncHandler(async (req, res) => {
  try {
    const db = await getDb();
    const code = req.params.code.trim();
    console.log('详情查询:', code);
    
    // 精确匹配
    let result = db.exec(`
      SELECT code, name, enName, status, ics, ccs, publishDate, implementDate, 
             department, manager, issuer, source_type, hcno
      FROM standards WHERE code = ?
    `, [code]);
    console.log('精确匹配:', result?.[0]?.values?.length || 0);
    
    // 如果没找到，尝试模糊匹配
    if (!result[0]?.values.length) {
      result = db.exec(`
        SELECT code, name, enName, status, ics, ccs, publishDate, implementDate, 
               department, manager, issuer, source_type, hcno
        FROM standards WHERE code LIKE ? LIMIT 1
      `, [code + '%']);
      console.log('模糊匹配:', result?.[0]?.values?.length || 0);
    }
    
    // 再尝试去掉空格的匹配
    if (!result[0]?.values.length) {
      const normalized = code.replace(/\s+/g, '');
      result = db.exec(`
        SELECT code, name, enName, status, ics, ccs, publishDate, implementDate, 
               department, manager, issuer, source_type, hcno
        FROM standards WHERE code LIKE ? LIMIT 1
      `, ['%' + normalized + '%']);
      console.log('去除空格匹配:', result?.[0]?.values?.length || 0);
    }
    
    if (!result[0]?.values.length) {
      return res.status(404).json({ error: '未找到该标准' });
    }
    const row = result[0].values[0];
    res.json({
      code: row[0], name: row[1], enName: row[2],
      status: row[3], ics: row[4], ccs: row[5],
      publishDate: row[6], implementDate: row[7],
      department: row[8], manager: row[9], issuer: row[10],
      source: row[11], hcno: row[12]
    });
  } catch (e) {
    res.status(500).json({ error: e.message });
  }
}));

/** 4. 统计 */
app.get('/api/stats', (req, res) => {
  try {
    const total = db.exec("SELECT COUNT(*) as c FROM standards")[0]?.values[0]?.[0] || 0;
    const icsCount = db.exec("SELECT COUNT(*) as c FROM standards WHERE ics != '' OR ccs != ''")[0]?.values[0]?.[0] || 0;
    const hbbaTotal = db.exec("SELECT COUNT(*) as c FROM standards WHERE source_type = 'hbba'")[0]?.values[0]?.[0] || 0;
    const hbbaFilled = db.exec("SELECT COUNT(*) as c FROM standards WHERE source_type = 'hbba' AND (ics != '' OR ccs != '')")[0]?.values[0]?.[0] || 0;
    const dbbaTotal = db.exec("SELECT COUNT(*) as c FROM standards WHERE source_type = 'dbba'")[0]?.values[0]?.[0] || 0;
    const dbbaFilled = db.exec("SELECT COUNT(*) as c FROM standards WHERE source_type = 'dbba' AND (ics != '' OR ccs != '')")[0]?.values[0]?.[0] || 0;
    const openstdTotal = db.exec("SELECT COUNT(*) as c FROM standards WHERE source_type = 'openstd'")[0]?.values[0]?.[0] || 0;
    const openstdFilled = db.exec("SELECT COUNT(*) as c FROM standards WHERE source_type = 'openstd' AND (ics != '' OR ccs != '')")[0]?.values[0]?.[0] || 0;
    const samrTotal = db.exec("SELECT COUNT(*) as c FROM standards WHERE source_type = 'samr'")[0]?.values[0]?.[0] || 0;
    const samrFilled = db.exec("SELECT COUNT(*) as c FROM standards WHERE source_type = 'samr' AND (ics != '' OR ccs != '')")[0]?.values[0]?.[0] || 0;
    const csresTotal = db.exec("SELECT COUNT(*) as c FROM standards WHERE source_type LIKE '%csres%'")[0]?.values[0]?.[0] || 0;
    const csresFilled = db.exec("SELECT COUNT(*) as c FROM standards WHERE source_type LIKE '%csres%' AND (ics != '' OR ccs != '')")[0]?.values[0]?.[0] || 0;
    
    res.json({
      total, icsCount,
      hbba: { total: hbbaTotal, filled: hbbaFilled, pct: hbbaTotal ? ((hbbaFilled/hbbaTotal)*100).toFixed(1) : '0.0' },
      dbba: { total: dbbaTotal, filled: dbbaFilled, pct: dbbaTotal ? ((dbbaFilled/dbbaTotal)*100).toFixed(1) : '0.0' },
      openstd: { total: openstdTotal, filled: openstdFilled, pct: openstdTotal ? ((openstdFilled/openstdTotal)*100).toFixed(1) : '0.0' },
      samr: { total: samrTotal, filled: samrFilled, pct: samrTotal ? ((samrFilled/samrTotal)*100).toFixed(1) : '0.0' },
      csres: { total: csresTotal, filled: csresFilled, pct: csresTotal ? ((csresFilled/csresTotal)*100).toFixed(1) : '0.0' }
    });
	  } catch(e) {
	    console.error("stats error:", e.message, e.stack);
	    res.status(500).json({ error: e.message });
	  }
});

/** 5. 标准系列分类 */
app.get('/api/series-stats', asyncHandler(async (req, res) => {
  try {
    const db = await getDb();
    const result = db.exec(`
      SELECT source_type as type, SUBSTR(code, 1, instr(code || '-', '-') - 1) as prefix, COUNT(*) as cnt
      FROM standards
      GROUP BY type, prefix
      HAVING cnt >= 10
      ORDER BY cnt DESC
      LIMIT 50
    `);
    res.json({ series: result[0]?.values.map(v => ({ type: v[0], prefix: v[1], count: v[2] })) || [] });
  } catch (e) {
    res.status(500).json({ error: e.message });
  }
}));

// ======= 聊天页面路由 =======
app.get('/chat', (req, res) => {
  res.sendFile(path.join(__dirname, 'public', 'chat.html'));
});

// 重定向根路径到聊天页面
app.get('/', (req, res) => {
  res.redirect('/chat');
});

// ======= 启动 =======
app.listen(PORT, async () => {
  console.log('='.repeat(50));
  console.log('  工程建设标准数据库 - AI对话服务 v2');
  console.log('='.repeat(50));
  console.log(`  AI 对话:   http://localhost:${PORT}/chat`);
  console.log(`  搜索 API:  http://localhost:${PORT}/api/search?q=混凝土`);
  console.log(`  统计:      http://localhost:${PORT}/api/stats`);
  console.log(`  LLM 模型:  ${LLM_MODEL}`);
  console.log(`  数据库:    ${DB_PATH}`);
  console.log('='.repeat(50));
  
  try {
    await getDb();
    console.log('✅ 数据库加载成功');
  } catch (e) {
    console.error('❌ 数据库加载失败:', e.message);
  }
});
