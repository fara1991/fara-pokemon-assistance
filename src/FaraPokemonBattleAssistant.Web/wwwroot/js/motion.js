// 操作に応じた動き。Razor が data 属性と最終状態（文字・style の幅など）を出し、ここでは「前の値から今の値へ」の動きだけを付ける。
//  data-count="数値"          数字を前の値から数え上げる（data-count-dec: 小数の桁数、data-count-from: 最初に出たときの開始値）
//  data-pop="キー"            キーが変わったらポンと出す
//  data-swap="キー"           キーが変わったら上から落ちて少し弾む（ポケモンの入れ替え）
//  data-bar                   style の width が変わったら前の長さから伸び縮みする（最初は 0 から）
//  data-hp-calc               ダメージ計算の HP バー（data-hp-part の left / width を動かす）
//  data-rolls="署名"          乱数のマスが署名が変わると順にめくれる
//  data-flip-group / data-flip-key  子のキー付き要素が並び替わったら、前の位置から滑らかに動く（FLIP）
//  data-motion-static         この中は、新しく出てきたときに動かさない（スマホの結果バーを開いたとき）
// Blazor が持っているテキストノードや class / style は壊さない（テキストは nodeValue だけ、動きは WAAPI だけで付ける）。
// prefers-reduced-motion: reduce のときは何も動かさない（Razor が出した最終状態のまま）。
(function () {
    "use strict";
    var EASE = "cubic-bezier(.16, 1, .3, 1)";
    var html = document.documentElement;
    var mq = window.matchMedia("(prefers-reduced-motion: reduce)");
    var reduce = function () { return mq.matches; };
    var lastNav = performance.now();

    function isStatic(el) { return !!(el.closest && el.closest("[data-motion-static]")); }
    // ページを開いた直後に一斉に出てくる要素は動かさない（操作したあとに出てきた要素だけ動かす）
    function settled() { return performance.now() - lastNav > 900; }

    // ---------- 数え上げ ----------
    var counts = new WeakMap();
    function scanCount(el) {
        var target = parseFloat(el.getAttribute("data-count"));
        var node = el.firstChild;
        if (!isFinite(target) || !node || node.nodeType !== 3 || el.childNodes.length !== 1) return;
        var s = counts.get(el);
        var from;
        if (!s) {
            s = { target: target, shown: target, raf: 0 };
            counts.set(el, s);
            var f = el.getAttribute("data-count-from");
            if (f === null || reduce() || isStatic(el)) return;
            from = parseFloat(f);
        } else {
            if (s.target === target) return;
            cancelAnimationFrame(s.raf);
            s.target = target;
            if (reduce()) { s.shown = target; return; }
            from = s.shown;
        }
        if (!isFinite(from) || from === target) { s.shown = target; return; }
        var dec = parseInt(el.getAttribute("data-count-dec") || "0", 10);
        var finalText = node.nodeValue;     // Blazor が出した最終の文字（最後はこれに戻す）
        var t0 = performance.now(), dur = 700;
        var write = function (text) { var n = el.firstChild; if (n && n.nodeType === 3) n.nodeValue = text; };
        write(from.toFixed(dec));
        s.shown = from;
        var step = function (now) {
            var p = Math.min(1, (now - t0) / dur), e = 1 - Math.pow(1 - p, 3);
            if (p < 1) {
                s.shown = from + (target - from) * e;
                write(s.shown.toFixed(dec));
                s.raf = requestAnimationFrame(step);
            } else {
                s.shown = target;
                write(finalText);
            }
        };
        s.raf = requestAnimationFrame(step);
    }

    // ---------- ポン / 入れ替え ----------
    var keyed = new WeakMap();
    function keyChanged(el, key, allowFresh) {
        var prev = keyed.get(el);
        keyed.set(el, key);
        if (reduce()) return false;
        if (prev === undefined) return allowFresh && settled() && !isStatic(el);
        return prev !== key;
    }
    function scanPop(el) {
        if (!keyChanged(el, el.getAttribute("data-pop"), true)) return;
        el.animate([
            { transform: "scale(.6)", opacity: 0 },
            { transform: "scale(1.08)", opacity: 1, offset: 0.6 },
            { transform: "scale(1)", opacity: 1 }
        ], { duration: 500, easing: EASE });
    }
    function scanSwap(el) {
        if (!keyChanged(el, el.getAttribute("data-swap"), false)) return;
        el.animate([
            { transform: "translateY(-14px) scale(.85)", opacity: 0 },
            { transform: "translateY(2px) scale(1.04)", opacity: 1, offset: 0.7 },
            { transform: "none", opacity: 1 }
        ], { duration: 550, easing: EASE });
    }

    // ---------- 伸び縮みする要素（left / width の style） ----------
    // 動いている途中なら、今見えている位置（px）から続きを動かす
    // （色の CSS transition は数えない。動かしているのは WAAPI のものだけ）
    function running(el) {
        return el.getAnimations().filter(function (a) { return !(window.CSSTransition && a instanceof CSSTransition); });
    }
    function currentOf(el, props, fallback) {
        if (running(el).length === 0) return fallback;
        var cs = getComputedStyle(el), o = {};
        props.forEach(function (p) { o[p] = cs[p]; });
        return o;
    }
    function run(el, from, to, opts) {
        running(el).forEach(function (a) { a.cancel(); });
        var a = {}, b = {};
        Object.keys(to).forEach(function (k) { if (from[k] !== to[k]) { a[k] = from[k]; b[k] = to[k]; } });
        if (Object.keys(b).length === 0) return;
        el.animate([a, b], opts);
    }

    var bars = new WeakMap();
    function scanBar(el) {
        var to = { width: el.style.width };
        var prev = bars.get(el);
        bars.set(el, to);
        if (reduce()) return;
        var from = prev ? currentOf(el, ["width"], prev) : { width: "0%" };
        run(el, from, to, { duration: 800, easing: EASE });
    }

    // ---------- ダメージ計算の HP バー ----------
    var hps = new WeakMap();
    function scanHp(root) {
        var parts = {};
        root.querySelectorAll("[data-hp-part]").forEach(function (p) { parts[p.getAttribute("data-hp-part")] = p; });
        var key = root.getAttribute("data-hp-key");
        var immune = root.getAttribute("data-hp-immune") === "1";
        var vals = {};
        Object.keys(parts).forEach(function (k) { vals[k] = { left: parts[k].style.left, width: parts[k].style.width }; });
        var sig = JSON.stringify(vals);
        var prev = hps.get(root);
        hps.set(root, { key: key, immune: immune, vals: vals, sig: sig });
        if (reduce()) return;
        var statics = isStatic(root);
        if (!prev && statics) return;
        if (prev && prev.key === key && prev.sig === sig) return;
        var full = !prev || prev.key !== key;      // 最初と防御側が変わったときは、満タン（今の HP）から減らす
        var start = root.getAttribute("data-hp-start") || "100%";
        var startVals = {
            gone: { left: start, width: "0%" },
            fill: { left: "0%", width: start },
            range: { left: start, width: "0%" }
        };
        Object.keys(parts).forEach(function (k) {
            var from = full ? startVals[k] : currentOf(parts[k], ["left", "width"], prev.vals[k]);
            run(parts[k], from, vals[k], { duration: 1100, delay: full ? 150 : 0, easing: EASE, fill: "backwards" });
        });
        var bar = root.querySelector(".hp-calc-bar");
        if (prev && bar) {
            bar.animate([
                { transform: "none" },
                { transform: "translateX(-2px)", boxShadow: "0 0 0 3px rgba(229, 72, 77, .35)", offset: 0.3 },
                { transform: "translateX(2px)", offset: 0.6 },
                { transform: "none" }
            ], { duration: 350 });
        }
        if (prev && immune && !prev.immune) {
            root.animate([
                { transform: "none" },
                { transform: "translateX(-5px)", offset: 0.2 },
                { transform: "translateX(4px)", offset: 0.4 },
                { transform: "translateX(-3px)", offset: 0.6 },
                { transform: "translateX(2px)", offset: 0.8 },
                { transform: "none" }
            ], { duration: 450 });
        }
    }

    // ---------- 乱数のマス ----------
    var rolls = new WeakMap();
    function scanRolls(el) {
        var sig = el.getAttribute("data-rolls");
        var prev = rolls.get(el);
        rolls.set(el, sig);
        if (reduce() || prev === sig) return;
        if (prev === undefined && isStatic(el)) return;
        Array.prototype.forEach.call(el.children, function (c, i) {
            c.animate([
                { transform: "perspective(300px) rotateX(80deg)", opacity: 0 },
                { transform: "none", opacity: 1 }
            ], { duration: 450, delay: i * 28, easing: EASE, fill: "backwards" });
        });
    }

    // ---------- 並び替えの動き（FLIP） ----------
    // 要素ではなくキー（タイプ名など）で位置を覚える。Blazor は要素を位置で使い回すので、要素に覚えさせない。
    var flips = new WeakMap();
    function measure(group) {
        var m = new Map(), any = false;
        group.querySelectorAll("[data-flip-key]").forEach(function (el) {
            if (el.offsetParent === null) return;      // 表示されていない（PC では非表示の一覧など）
            any = true;
            // 親（position: relative のグループ）からの位置。transform の途中でも動かない値を使う
            var x = 0, y = 0, n = el;
            while (n && n !== group) { x += n.offsetLeft; y += n.offsetTop; n = n.offsetParent; }
            m.set(el.getAttribute("data-flip-key"), { el: el, x: x, y: y });
        });
        return any ? m : null;
    }
    function scanFlip(group) {
        var now = measure(group);
        var prev = flips.get(group);
        flips.set(group, now);
        if (!now || !prev || reduce()) return;
        var i = 0;
        now.forEach(function (n, key) {
            var p = prev.get(key);
            if (!p) return;
            var dx = p.x - n.x, dy = p.y - n.y;
            if (Math.abs(dx) < 1 && Math.abs(dy) < 1) return;
            n.el.animate([
                { transform: "translate(" + dx + "px, " + dy + "px)" },
                { transform: "none" }
            ], { duration: 650, delay: Math.min(i * 12, 240), easing: EASE, fill: "backwards" });
            i++;
        });
    }

    // ---------- サイドバーの選択中の印 ----------
    var navState = { placed: false, x: 0, y: 0, w: 0, h: 0 };
    function scanNav(instant) {
        var nav = document.querySelector(".side-nav");
        var ind = nav && nav.querySelector(".nav-indicator");
        if (!ind) return;
        if (nav.getClientRects().length === 0) { navState.placed = false; return; }   // 閉じたスマホのメニューは測れない
        var a = nav.querySelector("a.active");
        if (!a) { ind.classList.remove("on"); return; }
        var nr = nav.getBoundingClientRect(), ar = a.getBoundingClientRect();
        var x = ar.left - nr.left, y = ar.top - nr.top, w = ar.width, h = ar.height;
        if (navState.placed && x === navState.x && y === navState.y && w === navState.w && h === navState.h && ind.classList.contains("on")) return;
        var still = instant || !navState.placed || reduce();
        if (still) ind.classList.add("no-anim");
        ind.style.transform = "translate(" + x + "px, " + y + "px)";
        ind.style.width = w + "px";
        ind.style.height = h + "px";
        if (still) { void ind.offsetWidth; ind.classList.remove("no-anim"); }
        ind.classList.add("on");
        html.classList.add("motion-nav");
        navState = { placed: true, x: x, y: y, w: w, h: h };
    }

    // ---------- まとめて見る ----------
    function scan() {
        document.querySelectorAll("[data-flip-group]").forEach(scanFlip);
        document.querySelectorAll("[data-count]").forEach(scanCount);
        document.querySelectorAll("[data-pop]").forEach(scanPop);
        document.querySelectorAll("[data-swap]").forEach(scanSwap);
        document.querySelectorAll("[data-bar]").forEach(scanBar);
        document.querySelectorAll("[data-hp-calc]").forEach(scanHp);
        document.querySelectorAll("[data-rolls]").forEach(scanRolls);
        scanNav(false);
    }

    function start() {
        var root = document.getElementById("app") || document.body;
        new MutationObserver(function (records) {
            // 選んでいる印を動かしたこと自体では、もう一度見ない
            var own = records.every(function (r) { return r.target.classList && r.target.classList.contains("nav-indicator"); });
            if (!own) scan();
        }).observe(root, {
            childList: true, subtree: true, attributes: true,
            attributeFilter: ["class", "style", "data-count", "data-pop", "data-swap", "data-rolls", "data-hp-key", "data-hp-immune", "data-hp-start", "data-flip-key"]
        });
        window.addEventListener("resize", function () {
            document.querySelectorAll("[data-flip-group]").forEach(function (g) { flips.set(g, measure(g)); });
            scanNav(true);
        });
        scan();
    }

    // ---------- ページの切り替え（View Transitions。非対応のブラウザでは何もしない） ----------
    ["pushState", "replaceState"].forEach(function (name) {
        var orig = history[name];
        history[name] = function () { lastNav = performance.now(); return orig.apply(this, arguments); };
    });
    window.addEventListener("popstate", function () { lastNav = performance.now(); });

    document.addEventListener("click", function (e) {
        if (!document.startViewTransition || reduce()) return;
        if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
        var a = e.target.closest && e.target.closest("a[href]");
        if (!a || (a.target && a.target !== "_self") || a.hasAttribute("download") || a.closest('[aria-disabled="true"]')) return;
        var u;
        try { u = new URL(a.href, location.href); } catch (err) { return; }
        if (u.origin !== location.origin || u.pathname === location.pathname) return;
        // Blazor に任せると、前の画面を撮る前にページが切り替わってしまう。
        // そこでこのクリックでの切り替えは止めて（Blazor のリンク処理は defaultPrevented を見て何もしない）、
        // 前の画面を撮ったあとに Blazor.navigateTo で切り替える。メニューを閉じるなどの @onclick は、止めないのでそのまま動く。
        if (!window.Blazor || typeof window.Blazor.navigateTo !== "function") return;
        e.preventDefault();
        html.classList.add("vt-nav");
        var t = document.startViewTransition(function () {
            return new Promise(function (resolve) {
                var t0 = performance.now(), lastMut = t0;
                var obs = new MutationObserver(function () { lastMut = performance.now(); });
                obs.observe(document.getElementById("app") || document.body, { childList: true, subtree: true });
                window.Blazor.navigateTo(u.href);
                // 切り替わって描画が落ち着くまで待つ（長くても 0.8 秒）
                var timer = setInterval(function () {
                    var now = performance.now();
                    if ((location.pathname === u.pathname && now - lastMut > 60) || now - t0 > 800) {
                        clearInterval(timer); obs.disconnect(); resolve();
                    }
                }, 25);
            });
        });
        var done = function () { html.classList.remove("vt-nav"); };
        t.finished.then(done, done);
    }, true);

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
})();
