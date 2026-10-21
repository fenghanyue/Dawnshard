(() => {
  "use strict";

  // The client supplies its selection in a query string or a #detail/<id> fragment.
  // Only inspect selection fields: session / language / time-zone values are not IDs.
  const idKeys = [
    "id",
    "quest_id",
    "questId",
    "quest_group_id",
    "questGroupId",
    "gid",
  ];

  function getRequestedId() {
    const query = new URLSearchParams(window.location.search);
    for (const key of idKeys) {
      if (query.has(key)) return query.get(key);
    }

    const fragment = window.location.hash.slice(1);
    const detail = fragment.match(/^\/?detail\/(\d+)(?:[/?&]|$)/);
    if (detail) return detail[1];

    const hashQuery = new URLSearchParams(fragment.replace(/^\?/, ""));
    for (const key of idKeys) {
      if (hashQuery.has(key)) return hashQuery.get(key);
    }
    return null;
  }

  function updateSelection() {
    const requestedId = getRequestedId();
    // Master data: group 21001, including its solo and co-op quests (21001xxxx).
    const isMidgardsormr = /^21001(?:\d{2}|\d{4})?$/.test(requestedId || "");
    const title = isMidgardsormr ? "真耶梦加得 · 技能攻略" : "Boss 技能攻略";
    document.title = title;
    document.getElementById("page-title").textContent = title;
    document.getElementById("selection-notice").hidden = isMidgardsormr;

    document.getElementById("selection-message").textContent =
      requestedId === null
        ? "目前已收录下方的真风龙攻略。点击名称即可查看。"
        : "暂未收录所选 Boss 的攻略，或未能识别关卡。下方为已收录的真风龙攻略，请先核对 Boss 名称。";
  }

  updateSelection();
  window.addEventListener("hashchange", updateSelection);
})();
