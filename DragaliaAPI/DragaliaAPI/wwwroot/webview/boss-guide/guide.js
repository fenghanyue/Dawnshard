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
  // Master data: group IDs and their solo / co-op quests (21001xxxx / 21003xxxx).
  const guides = [
    {
      anchor: "high-midgardsormr",
      name: "真耶梦加得",
      ids: /^21001(?:\d{2}|\d{4})?$/,
    },
    {
      anchor: "high-brunhilda",
      name: "真布伦希尔德",
      ids: /^21003(?:\d{2}|\d{4})?$/,
    },
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
    // A directory link is an explicit selection, including when the URL has an ID.
    const selectedGuide =
      guides.find((guide) => guide.anchor === window.location.hash.slice(1)) ||
      guides.find((guide) => guide.ids.test(requestedId || ""));
    const title = selectedGuide
      ? `${selectedGuide.name} · 技能攻略`
      : "Boss 技能攻略";
    document.title = title;
    document.getElementById("page-title").textContent = title;

    for (const guide of guides) {
      document.getElementById(guide.anchor).hidden = guide !== selectedGuide;
      const link = document.querySelector(`nav a[href="#${guide.anchor}"]`);
      if (guide === selectedGuide) link.setAttribute("aria-current", "page");
      else link.removeAttribute("aria-current");
    }

    const message = document.getElementById("selection-message");
    message.hidden = Boolean(selectedGuide);
    message.textContent =
      requestedId === null
        ? "请选择要查看的 Boss 攻略。"
        : "暂未收录所选 Boss 的攻略，或未能识别关卡。请从目录选择已收录攻略。";
  }

  updateSelection();
  window.addEventListener("hashchange", updateSelection);
})();
