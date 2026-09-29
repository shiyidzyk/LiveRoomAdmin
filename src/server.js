const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { exec, spawn } = require('child_process');

const PORT = 3000;
const ROOT = __dirname;
const ACCOUNTS_FILE = path.join(ROOT, 'accounts.json');
const LOGS = {
  mediamtx: path.join(ROOT, 'mediamtx.log'),
  server: path.join(ROOT, 'server.log'),
  ffmpeg: path.join(ROOT, 'ffmpeg.log')
};

// 多端登录策略：multiEnabled=是否允许多端（默认 false=单端）；maxDevices=允许同时登录的设备数（默认1）
const LOGIN_POLICY_FILE = path.join(ROOT, 'login_policy.json');
let loginPolicy = { multiEnabled: false, maxDevices: 1 };
try { loginPolicy = Object.assign({ multiEnabled: false, maxDevices: 1 }, JSON.parse(fs.readFileSync(LOGIN_POLICY_FILE, 'utf8'))); } catch (e) {}
function saveLoginPolicy() { fs.writeFileSync(LOGIN_POLICY_FILE, JSON.stringify(loginPolicy, null, 2)); };

// ---------- accounts ----------
let accounts = {};
try { accounts = JSON.parse(fs.readFileSync(ACCOUNTS_FILE, 'utf8')); } catch (e) {}
function saveAccounts() { fs.writeFileSync(ACCOUNTS_FILE, JSON.stringify(accounts, null, 2)); }
function hashPass(pw, salt) {
    return crypto.scryptSync(pw, salt, 32).toString('hex');
}
// bootstrap admin（最高管理员）
if (!accounts['admin']) {
    const salt = crypto.randomBytes(8).toString('hex');
    accounts['admin'] = { salt, hash: hashPass('admin123', salt), role: 'superadmin', tokens: 0, banned: false, createdAt: Date.now() };
    saveAccounts();
    console.log('Bootstrap superadmin admin/admin123 created');
} else if (accounts['admin'].role !== 'superadmin') {
    // 旧版本迁移：admin 账号升级为最高管理员
    accounts['admin'].role = 'superadmin';
    saveAccounts();
    console.log('Migrated admin -> superadmin');
}

// ---------- 权限体系 ----------
// 角色等级：superadmin(最高管理员) > admin(管理员) > user(观众)
const ROLE_LEVEL = { superadmin: 3, admin: 2, user: 1 };
const ROLE_NAMES = { superadmin: '最高管理员', admin: '管理员', user: '观众' };
// 管理员细分权限项
const PERM_KEYS = ['createUser', 'editRole', 'banUser', 'kickUser', 'warnUser', 'roomControl', 'resetPass', 'deleteUser', 'sessionMgr', 'deviceKick'];
// sessionMgr=多端登录策略设置（开关/数量，默认关）；deviceKick=在线设备强制下线（默认开，最高管理员可单独关闭）
const DEFAULT_ADMIN_PERMS = { createUser: true, banUser: true, kickUser: true, warnUser: true, roomControl: true, resetPass: true, deleteUser: false, editRole: false, sessionMgr: false, deviceKick: true };

function getPerms(acc) {
    if (!acc) return {};
    if (acc.role === 'superadmin') { const p = {}; for (const k of PERM_KEYS) p[k] = true; return p; }
    if (acc.role === 'admin') return Object.assign({}, DEFAULT_ADMIN_PERMS, acc.perms || {});
    return {};
}
function hasPerm(s, key) { return getPerms(accounts[s.username])[key] === true; }
function roleLevel(u) { return ROLE_LEVEL[(accounts[u] || {}).role] || 0; }
/** 操作者能否操作目标：只能操作比自己等级低的账号 */
function canOperate(s, target) { return roleLevel(target) < roleLevel(s.username); }

// ---------- sessions ----------
const sessions = new Map(); // token -> {username, role, name, ip, ua, device, loginAt}
const roomState = { stopped:false, message:'' };
function makeToken() { return crypto.randomBytes(24).toString('hex'); }
function clientIp(req) { return (req.headers['x-forwarded-for'] || req.socket.remoteAddress || '').split(',')[0].trim(); }
/** 某用户当前全部有效会话 token */
function sessionsOf(username) {
    const out = [];
    for (const [tok, ss] of sessions) if (ss.username === username) out.push(tok);
    return out;
}
/** 当前生效的同时登录设备上限（关闭多端时恒为 1） */
function deviceLimit() {
    if (!loginPolicy.multiEnabled) return 1;
    const n = parseInt(loginPolicy.maxDevices, 10);
    return Number.isFinite(n) && n >= 1 ? n : 1;
}

// ---------- live users / sse ----------
const sseClients = new Set();
// token -> { username, res, role, tokens }（多设备：同一用户可有多条）
const activeViewers = new Map();
/** 某用户是否在线（任一设备连着 SSE） */
function isOnline(username) {
    for (const v of activeViewers.values()) if (v.username === username) return true;
    return false;
}

function broadcastViewers() {
    // 多设备下按 username 去重
    const seen = new Set(); const list = [];
    for (const v of activeViewers.values()) {
        if (!seen.has(v.username)) { seen.add(v.username); list.push({ name: v.username, role: v.role }); }
    }
    broadcast('viewers', { count: list.length, list });
}
function broadcast(event, data) {
    const msg = `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
    for (const c of sseClients) { try { c.write(msg); } catch (e) {} }
}
function sendTo(username, event, data) {
    // 发给该用户的全部在线设备
    for (const [tok, v] of activeViewers) {
        if (v.username === username) { try { v.res.write(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`); } catch (e) {} }
    }
}

/**
 * 强制某用户（全部设备）下线：给其每条 SSE 发事件（携带各自会话标识 sess），
 * 随后断开 SSE、清除该用户全部会话，需重新登录。用于封禁/删号/角色变更/单端顶号。
 */
function forceKick(username, message, eventName = 'kicked') {
    const toks = sessionsOf(username);
    for (const [tok, v] of [...activeViewers]) {
        if (v.username === username) {
            try { v.res.write('event: ' + eventName + '\ndata: ' + JSON.stringify({ message: message || '', sess: tok.slice(0, 8) }) + '\n\n'); } catch (e) {}
        }
    }
    setTimeout(() => {
        for (const [tok, v] of [...activeViewers]) {
            if (v.username === username) { try { v.res.end(); } catch (e) {} activeViewers.delete(tok); }
        }
        broadcastViewers();
    }, 300);
    for (const tok of toks) sessions.delete(tok);
}

/**
 * 强制下线指定设备（单条会话）：仅给该 token 的 SSE 发 kicked 并删除该会话，不影响同账号其他设备。
 */
function kickToken(token, message) {
    const v = activeViewers.get(token);
    if (v) {
        try { v.res.write('event: kicked\ndata: ' + JSON.stringify({ message: message || '', sess: token.slice(0, 8) }) + '\n\n'); } catch (e) {}
        setTimeout(() => { try { v.res.end(); } catch (e) {} activeViewers.delete(token); broadcastViewers(); }, 300);
    }
    sessions.delete(token);
}

/**
 * 登录成功后登记会话，按多端策略处理：
 *  - 设备上限=1（关闭多端）：顶掉该用户旧会话（顶号提示）
 *  - 设备上限>1：未满则新增；已满则拒绝（limitReached）
 */
function registerSession(username, role, req, device) {
    const ip = clientIp(req);
    const ua = req.headers['user-agent'] || '';
    const limit = deviceLimit();
    const existing = sessionsOf(username);
    if (existing.length >= limit) {
        return { ok: false, limitReached: true, msg: '该账号已达同时登录设备上限（' + limit + ' 台），请先从已登录设备退出，或联系管理员下线设备' };
    }
    if (limit === 1 && existing.length > 0) {
        forceKick(username, '你的账号在其他设备(' + ip + ')登录，你已被下线');
    }
    const token = makeToken();
    sessions.set(token, { username, role, name: username, ip, ua, device: device || '', loginAt: Date.now() });
    return { ok: true, token };
}

function authUser(req) {
    const h = req.headers['authorization'] || '';
    const tok = h.startsWith('Bearer ') ? h.slice(7) : null;
    const s = tok && sessions.get(tok);
    return s || null;
}
function json(res, code, obj) {
    res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8', 'Access-Control-Allow-Origin': '*' });
    res.end(JSON.stringify(obj));
}
function readBody(req) {
    return new Promise((resolve) => {
        let b = ''; req.on('data', c => b += c); req.on('end', () => { try { resolve(JSON.parse(b)); } catch (e) { resolve({}); } });
    });
}

const GIFTS = {
    rose: { name: '玫瑰', price: 1, icon: '🌹', duration: 3000 },
    coffee: { name: '咖啡', price: 5, icon: '☕', duration: 4000 },
    cake: { name: '蛋糕', price: 20, icon: '🎂', duration: 5000 },
    rocket: { name: '火箭', price: 100, icon: '🚀', duration: 8000 },
    crown: { name: '皇冠', price: 520, icon: '👑', duration: 10000 },
    diamond: { name: '钻石', price: 1314, icon: '💎', duration: 12000 }
};
const startedAt = Date.now();

const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, `http://${req.headers.host}`);
    const p = url.pathname;

    // ---------- public: login ----------
    if (p === '/api/login' && req.method === 'POST') {
        const { username, password, device } = await readBody(req);
        const acc = accounts[username];
        if (!acc || acc.banned) return json(res, 401, { code: 1, msg: '账号不存在或已被封禁' });
        if (hashPass(password || '', acc.salt) !== acc.hash) return json(res, 401, { code: 1, msg: '密码错误' });
        const r = registerSession(username, acc.role, req, device);
        if (!r.ok) return json(res, 403, { code: 1, msg: r.msg });
        json(res, 200, { code: 0, data: { token: r.token, username, role: acc.role, tokens: acc.tokens } });
        return;
    }

    // ---------- SSE ----------
    if (p === '/api/events' && req.method === 'GET') {
        const tok = url.searchParams.get('token');
        const s = tok && sessions.get(tok);
        if (!s) { res.writeHead(401); res.end(); return; }
        res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache', 'Connection': 'keep-alive', 'Access-Control-Allow-Origin': '*' });
        res.write('event: connected\ndata: {"msg":"ok"}\n\n');
        sseClients.add(res);
        res.write(`event: roomstate\ndata: ${JSON.stringify(roomState)}\n\n`);
        activeViewers.set(tok, { username: s.username, res: res, role: s.role, tokens: accounts[s.username].tokens });
        broadcast('system', { type: 'join', name: s.username });
        broadcastViewers();
        // SSE 断开：仅移除该设备的在线状态，保留登录会话（可重连 SSE）
        const cleanup = () => { sseClients.delete(res); activeViewers.delete(tok); broadcastViewers(); };
        res.on('error', cleanup);
        req.on('close', cleanup);
        return;
    }

    if (p === '/api/room-state' && req.method === 'GET') { json(res, 200, { code:0, data: roomState }); return; }
    // ---------- user API ----------
    if (p === '/api/balance') {
        const s = authUser(req); if (!s) return json(res, 401, { code: 1, msg: '未登录' });
        json(res, 200, { code: 0, data: { tokens: accounts[s.username].tokens } }); return;
    }
    if (p === '/api/recharge' && req.method === 'POST') {
        const s = authUser(req); if (!s) return json(res, 401, { code: 1, msg: '未登录' });
        const { amount, method } = await readBody(req);
        const amt = Math.floor(amount || 0); if (amt <= 0) return json(res, 400, { code: 1, msg: '金额无效' });
        setTimeout(() => {
            accounts[s.username].tokens += amt; saveAccounts();
            broadcast('recharge', { name: s.username, amount: amt, tokens: accounts[s.username].tokens, method });
            json(res, 200, { code: 0, data: { tokens: accounts[s.username].tokens, amount: amt } });
        }, 1500);
        return;
    }
    if (p === '/api/gift' && req.method === 'POST') {
        const s = authUser(req); if (!s) return json(res, 401, { code: 1, msg: '未登录' });
        const { giftId } = await readBody(req);
        const g = GIFTS[giftId]; if (!g) return json(res, 400, { code: 1, msg: '礼物不存在' });
        const acc = accounts[s.username];
        if (acc.tokens < g.price) return json(res, 400, { code: 1, msg: '代币不足' });
        acc.tokens -= g.price; saveAccounts();
        broadcast('gift', { name: s.username, giftName: g.name, giftIcon: g.icon, price: g.price, duration: g.duration, tokens: acc.tokens });
        json(res, 200, { code: 0, data: { tokens: acc.tokens } });
        return;
    }
    if (p === '/api/danmaku' && req.method === 'POST') {
        const s = authUser(req); if (!s) return json(res, 401, { code: 1, msg: '未登录' });
        const { text } = await readBody(req);
        if (!text || !text.trim()) return json(res, 400, { code: 1, msg: '空消息' });
        broadcast('danmaku', { name: s.username, text: text.trim().slice(0, 200), color: '#' + crypto.randomBytes(3).toString('hex') });
        json(res, 200, { code: 0 });
        return;
    }
    if (p === '/api/gifts') { json(res, 200, { code: 0, data: GIFTS }); return; }
    if (p === '/api/stream') {
        const host = (req.headers.host || 'localhost:3000').split(':')[0];
        json(res, 200, { code: 0, data: {
            hls: `http://${host}:8888/stream/test/index.m3u8`,
            webrtc: `http://${host}:8889/lowlatency/whep`,
            rtmp: `rtmp://${host}:1935/stream`
        }});
        return;
    }

    // ---------- admin API ----------
    if (p === '/admin/login' && req.method === 'POST') {
        const { username, password, device } = await readBody(req);
        const acc = accounts[username];
        if (!acc || !ROLE_LEVEL[acc.role] || acc.role === 'user') return json(res, 403, { code: 1, msg: '非管理员' });
        if (hashPass(password || '', acc.salt) !== acc.hash) return json(res, 401, { code: 1, msg: '密码错误' });
        const r = registerSession(username, acc.role, req, device);
        if (!r.ok) return json(res, 403, { code: 1, msg: r.msg });
        json(res, 200, { code: 0, data: { token: r.token, username, role: acc.role, roleName: ROLE_NAMES[acc.role] || acc.role, perms: getPerms(acc) } });
        return;
    }
    if (p.startsWith('/admin/api/')) {
        const s = authUser(req);
        if (!s) return json(res, 401, { code: 1, msg: '未登录' });
        const me = accounts[s.username];
        if (!me || !ROLE_LEVEL[me.role]) return json(res, 403, { code: 1, msg: '需要管理员权限' });

        if (p === '/admin/api/overview' && req.method === 'GET') {
            const seen = new Set(); const list = [];
            for (const v of activeViewers.values()) if (!seen.has(v.username)) { seen.add(v.username); list.push({ name: v.username, role: v.role }); }
            json(res, 200, { code: 0, data: { online: list.length, viewers: list, uptime: Math.floor((Date.now() - startedAt) / 1000), accounts: Object.keys(accounts).length } });
            return;
        }
        if (p === '/admin/api/me' && req.method === 'GET') {
            json(res, 200, { code: 0, data: { username: s.username, role: me.role, roleName: ROLE_NAMES[me.role] || me.role, perms: getPerms(me) } });
            return;
        }

        // ---- 多端登录策略 ----
        const myTok = (req.headers['authorization'] || '').replace('Bearer ', '');
        if (p === '/admin/api/login-policy' && req.method === 'GET') {
            json(res, 200, { code: 0, data: { multiEnabled: !!loginPolicy.multiEnabled, maxDevices: deviceLimit() } });
            return;
        }
        if (p === '/admin/api/login-policy' && req.method === 'POST') {
            if (!hasPerm(s, 'sessionMgr')) return json(res, 403, { code: 1, msg: '没有多端登录设置权限（需最高管理员授予）' });
            const { multiEnabled, maxDevices } = await readBody(req);
            loginPolicy.multiEnabled = !!multiEnabled;
            let n = parseInt(maxDevices, 10);
            if (!Number.isFinite(n) || n < 1) n = 1;
            if (n > 100) n = 100;
            loginPolicy.maxDevices = n;
            saveLoginPolicy();
            json(res, 200, { code: 0, data: { multiEnabled: loginPolicy.multiEnabled, maxDevices: loginPolicy.maxDevices } });
            return;
        }
        // ---- 某用户的登录设备列表（带本机识别） ----
        if (p === '/admin/api/devices' && req.method === 'GET') {
            if (!hasPerm(s, 'deviceKick')) return json(res, 403, { code: 1, msg: '没有查看/管理在线设备的权限' });
            const username = url.searchParams.get('username');
            if (!accounts[username]) return json(res, 404, { code: 1, msg: '用户不存在' });
            const data = sessionsOf(username).map(tok => {
                const ss = sessions.get(tok);
                return { sess: tok.slice(0, 8), ip: ss.ip || '', ua: ss.ua || '', device: ss.device || '', loginAt: ss.loginAt || 0, online: activeViewers.has(tok), current: tok === myTok };
            });
            json(res, 200, { code: 0, data });
            return;
        }
        // ---- 强制下线指定设备（不影响同账号其他设备） ----
        if (p === '/admin/api/kick-device' && req.method === 'POST') {
            if (!hasPerm(s, 'deviceKick')) return json(res, 403, { code: 1, msg: '没有强制下线设备的权限' });
            const { username, sess } = await readBody(req);
            if (!accounts[username]) return json(res, 404, { code: 1, msg: '用户不存在' });
            const tok = sessionsOf(username).find(t => t.slice(0, 8) === sess);
            if (!tok) return json(res, 404, { code: 1, msg: '设备不存在或已下线' });
            if (tok === myTok) return json(res, 400, { code: 1, msg: '不能下线当前正在使用的设备' });
            kickToken(tok, '你的设备已被管理员强制下线');
            json(res, 200, { code: 0 });
            return;
        }
        if (p === '/admin/api/users' && req.method === 'GET') {
            const list = Object.entries(accounts).map(([u, a]) => ({ username: u, role: a.role, roleName: ROLE_NAMES[a.role] || a.role, perms: getPerms(a), tokens: a.tokens, banned: !!a.banned, online: isOnline(u) }));
            json(res, 200, { code: 0, data: list });
            return;
        }
        if (p === '/admin/api/users' && req.method === 'POST') {
            if (!hasPerm(s, 'createUser')) return json(res, 403, { code: 1, msg: '没有创建账号的权限' });
            const { username, password, tokens, role } = await readBody(req);
            if (!username || !password) return json(res, 400, { code: 1, msg: '用户名密码必填' });
            if (accounts[username]) return json(res, 400, { code: 1, msg: '用户已存在' });
            // 仅最高管理员可以创建管理员；普通管理员创建的一律为观众
            let newRole = role === 'admin' && me.role === 'superadmin' ? 'admin' : 'user';
            const salt = crypto.randomBytes(8).toString('hex');
            const rec = { salt, hash: hashPass(password, salt), role: newRole, tokens: Math.floor(tokens || 0), banned: false, createdAt: Date.now() };
            if (newRole === 'admin') rec.perms = Object.assign({}, DEFAULT_ADMIN_PERMS);
            accounts[username] = rec;
            saveAccounts();
            json(res, 200, { code: 0 });
            return;
        }
        if (p === '/admin/api/ban' && req.method === 'POST') {
            if (!hasPerm(s, 'banUser')) return json(res, 403, { code: 1, msg: '没有封禁权限' });
            const { username } = await readBody(req);
            if (!accounts[username]) return json(res, 404, { code: 1, msg: '用户不存在' });
            if (!canOperate(s, username)) return json(res, 400, { code: 1, msg: '不能封禁同级或更高级账号' });
            accounts[username].banned = true; saveAccounts();
            forceKick(username, '你的账号已被管理员封禁', 'banned');
            json(res, 200, { code: 0 });
            return;
        }
        if (p === '/admin/api/unban' && req.method === 'POST') {
            if (!hasPerm(s, 'banUser')) return json(res, 403, { code: 1, msg: '没有解封权限' });
            const { username } = await readBody(req);
            if (!accounts[username]) return json(res, 404, { code: 1, msg: '用户不存在' });
            if (!canOperate(s, username)) return json(res, 400, { code: 1, msg: '不能操作同级或更高级账号' });
            accounts[username].banned = false; saveAccounts();
            json(res, 200, { code: 0 });
            return;
        }
        if (p === '/admin/api/change-my-password' && req.method === 'POST') {
            const { oldPassword, newPassword } = await readBody(req);
            const acc = accounts[s.username];
            if (!acc) return json(res, 404, { code: 1, msg: '账号不存在' });
            if (hashPass(oldPassword || '', acc.salt) !== acc.hash) return json(res, 400, { code: 1, msg: '原密码错误' });
            const salt = crypto.randomBytes(8).toString('hex');
            acc.salt = salt; acc.hash = hashPass(newPassword, salt);
            saveAccounts();
            json(res, 200, { code: 0 });
            return;
        }
        if (p === '/admin/api/update-user' && req.method === 'POST') {
            // 重置密码 / 代币
            if (!hasPerm(s, 'resetPass')) return json(res, 403, { code: 1, msg: '没有重置密码权限' });
            const { username, password, tokens } = await readBody(req);
            if (!accounts[username]) return json(res, 404, { code: 1, msg: '用户不存在' });
            if (!canOperate(s, username)) return json(res, 400, { code: 1, msg: '不能操作同级或更高级账号' });
            if (password) {
                const salt = crypto.randomBytes(8).toString('hex');
                accounts[username].salt = salt;
                accounts[username].hash = hashPass(password, salt);
            }
            if (tokens !== undefined) accounts[username].tokens = Math.floor(tokens || 0);
            saveAccounts();
            json(res, 200, { code: 0 });
            return;
        }
        if (p === '/admin/api/set-role' && req.method === 'POST') {
            // 调整账号角色等级（仅最高管理员；保留至少一个最高管理员）
            if (me.role !== 'superadmin') return json(res, 403, { code: 1, msg: '仅最高管理员可调整角色' });
            const { username, role } = await readBody(req);
            if (!accounts[username]) return json(res, 404, { code: 1, msg: '用户不存在' });
            if (!ROLE_LEVEL[role] || role === 'superadmin') return json(res, 400, { code: 1, msg: '无效角色，只能调整为管理员或观众' });
            if (username === 'admin') return json(res, 400, { code: 1, msg: '不能修改最高管理员账号的角色' });
            if (role === 'user' && accounts[username].role === 'superadmin') return json(res, 400, { code: 1, msg: '不能降级最高管理员' });
            accounts[username].role = role;
            if (role === 'admin' && !accounts[username].perms) accounts[username].perms = Object.assign({}, DEFAULT_ADMIN_PERMS);
            saveAccounts();
            // 角色变更后踢下线重新登录
            forceKick(username, '你的账号权限已被调整，请重新登录');
            json(res, 200, { code: 0 });
            return;
        }
        if (p === '/admin/api/set-perms' && req.method === 'POST') {
            // 设置管理员的细分权限（仅最高管理员）
            if (me.role !== 'superadmin') return json(res, 403, { code: 1, msg: '仅最高管理员可设置权限' });
            const { username, perms } = await readBody(req);
            const acc = accounts[username];
            if (!acc) return json(res, 404, { code: 1, msg: '用户不存在' });
            if (acc.role !== 'admin') return json(res, 400, { code: 1, msg: '只能为管理员角色设置权限' });
            const p2 = {};
            for (const k of PERM_KEYS) p2[k] = !!(perms && perms[k]);
            acc.perms = p2;
            saveAccounts();
            json(res, 200, { code: 0 });
            return;
        }
        if (p === '/admin/api/delete-user' && req.method === 'POST') {
            if (!hasPerm(s, 'deleteUser')) return json(res, 403, { code: 1, msg: '没有删除账号权限' });
            const { username } = await readBody(req);
            if (!accounts[username]) return json(res, 404, { code: 1, msg: '用户不存在' });
            if (username === s.username) return json(res, 400, { code: 1, msg: '不能删除当前登录账号' });
            if (!canOperate(s, username)) return json(res, 400, { code: 1, msg: '不能删除同级或更高级账号' });
            if (accounts[username].role === 'superadmin') return json(res, 400, { code: 1, msg: '不能删除最高管理员账号' });
            delete accounts[username];
            saveAccounts();
            forceKick(username, '你的账号已被删除');
            json(res, 200, { code: 0 });
            return;
        }
        if (p === '/admin/api/warnroom' && req.method === 'POST') {
            if (!hasPerm(s, 'roomControl')) return json(res, 403, { code: 1, msg: '没有直播控制权限' });
            const { message } = await readBody(req);
            broadcast('roomwarn', { message: message || '直播间收到管理员警告' });
            json(res, 200, { code: 0 });
            return;
        }
        if (p === '/admin/api/kick' && req.method === 'POST') {
            if (!hasPerm(s, 'kickUser')) return json(res, 403, { code: 1, msg: '没有踢出权限' });
            const { username } = await readBody(req);
            if (!accounts[username]) return json(res, 404, { code: 1, msg: '用户不存在' });
            if (!canOperate(s, username)) return json(res, 400, { code: 1, msg: '不能踢出同级或更高级账号' });
            // 踢出并清除会话（需重新登录，不会自动恢复）
            forceKick(username, '你已被管理员踢出直播间');
            json(res, 200, { code: 0 });
            return;
        }
        if (p === '/admin/api/warn' && req.method === 'POST') {
            if (!hasPerm(s, 'warnUser')) return json(res, 403, { code: 1, msg: '没有警告权限' });
            const { username, message } = await readBody(req);
            if (!accounts[username]) return json(res, 404, { code: 1, msg: '用户不存在' });
            if (!canOperate(s, username)) return json(res, 400, { code: 1, msg: '不能警告同级或更高级账号' });
            sendTo(username, 'warn', { message: message || '管理员警告你请注意发言' });
            json(res, 200, { code: 0 });
            return;
        }
        if (p === '/admin/api/stop' && req.method === 'POST') {
            if (!hasPerm(s, 'roomControl')) return json(res, 403, { code: 1, msg: '没有直播控制权限' });
            // 停止低延迟 ffmpeg（切断直播播放）；Windows 下由客户端进程管理器兜底
            roomState.stopped=true; roomState.message='当前直播间已被管理员下播';
            broadcast('roomstop', { message: roomState.message });
            if (process.platform === 'win32') {
                exec('taskkill /F /IM ffmpeg.exe /T 2>nul', () => {});
            } else {
                exec('pkill -f ffmpeg', () => {});
            }
            json(res, 200, { code: 0 });
            return;
        }
        if (p === '/admin/api/resume' && req.method === 'POST') {
            if (!hasPerm(s, 'roomControl')) return json(res, 403, { code: 1, msg: '没有直播控制权限' });
            roomState.stopped=false; roomState.message='';
            // Windows 下 ffmpeg 循环由客户端 LiveServer 负责拉起（轮询 room-state）
            if (process.platform !== 'win32') {
                exec('pkill -f ffmpeg; sleep 1; nohup bash -c \'while true; do cd '+ROOT+' && ./ffmpeg -hide_banner -loglevel warning -fflags nobuffer -i rtmp://127.0.0.1:1935/stream/test -c:v copy -c:a opus -b:a 64k -strict -2 -rtsp_transport tcp -f rtsp rtsp://127.0.0.1:8554/lowlatency >> ffmpeg.log 2>&1; sleep 5; done\' >/dev/null 2>&1 &', () => {});
            }
            broadcast('roomresume', {});
            json(res, 200, { code: 0 });
            return;
        }
        if (p === '/admin/api/restart' && req.method === 'POST') {
            if (!hasPerm(s, 'roomControl')) return json(res, 403, { code: 1, msg: '没有直播控制权限' });
            const sh = path.join(ROOT, 'start_all.sh');
            roomState.stopped=false; roomState.message='';
            // Windows 下重启全部服务由客户端 LiveServer 执行
            if (process.platform !== 'win32') {
                exec('pkill -f mediamtx; pkill -f ffmpeg; sleep 1; setsid sh ' + sh + ' >/dev/null 2>&1 &', () => {});
            }
            json(res, 200, { code: 0 });
            return;
        }
        if (p === '/admin/api/logs' && req.method === 'GET') {
            const which = url.searchParams.get('file') || 'server';
            const f = LOGS[which] || LOGS.server;
            let out = '';
            try { out = fs.readFileSync(f, 'utf8'); } catch (e) { out = '(无日志)'; }
            const lines = out.split('\n').slice(-200).join('\n');
            json(res, 200, { code: 0, data: lines });
            return;
        }
    }

    // ---------- static ----------
    // 网页端管理页默认禁用；仅当 server/admin_web.json 标志文件存在时才放行（由桌面客户端设置页开关控制）
    if (p === '/admin.html' || p === '/admin' || p === '/admin/') {
        const flag = path.join(ROOT, 'admin_web.json');
        if (!fs.existsSync(flag)) {
            res.writeHead(403, { 'Content-Type': 'text/plain; charset=utf-8' });
            res.end('网页端管理页已关闭，请在桌面客户端设置页中开启');
            return;
        }
        const f = path.join(ROOT, 'public', p === '/admin.html' ? 'admin.html' : 'admin.html');
        fs.readFile(f, (err, data) => {
            if (err) { res.writeHead(404); res.end('not found'); return; }
            res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
            res.end(data);
        });
        return;
    }
    let filePath = path.join(ROOT, 'public', p === '/' ? 'index.html' : p);
    fs.readFile(filePath, (err, data) => {
        if (err) { res.writeHead(404); res.end('Not found'); return; }
        const ext = path.extname(filePath).toLowerCase();
        const types = { '.html': 'text/html; charset=utf-8', '.js': 'application/javascript', '.css': 'text/css' };
        res.writeHead(200, { 'Content-Type': types[ext] || 'application/octet-stream' });
        res.end(data);
    });
});

server.listen(PORT, '0.0.0.0', () => console.log(`Live server on http://0.0.0.0:${PORT}`));
