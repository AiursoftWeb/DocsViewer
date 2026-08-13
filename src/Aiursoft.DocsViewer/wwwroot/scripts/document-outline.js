(function () {
    "use strict";

    const outline = document.querySelector("[data-document-outline]");
    const content = document.querySelector(".markdown-content");
    if (!outline || !content) {
        return;
    }

    const list = outline.querySelector(".document-outline-list");
    const headings = Array.from(content.querySelectorAll("h1, h2, h3"))
        .filter(heading => heading.textContent.trim().length > 0);
    if (!list || headings.length === 0) {
        return;
    }

    const usedIds = new Set(Array.from(document.querySelectorAll("[id]"))
        .map(element => element.id)
        .filter(Boolean));
    const links = new Map();

    headings.forEach((heading, index) => {
        if (!heading.id) {
            heading.id = createHeadingId(heading.textContent, index, usedIds);
        }

        heading.classList.add("document-outline-target");

        const item = document.createElement("li");
        item.className = `document-outline-level-${heading.tagName.substring(1)}`;

        const link = document.createElement("a");
        link.href = `#${encodeURIComponent(heading.id)}`;
        link.textContent = heading.textContent.trim();
        link.title = link.textContent;
        link.addEventListener("click", event => {
            event.preventDefault();
            heading.scrollIntoView({
                behavior: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth",
                block: "start"
            });
            history.pushState(null, "", link.href);
            setActiveHeading(heading);
        });

        item.appendChild(link);
        list.appendChild(item);
        links.set(heading, link);
    });

    outline.hidden = false;
    outline.closest(".document-reading-layout")?.classList.add("has-document-outline");

    let updateScheduled = false;
    const scheduleActiveHeadingUpdate = () => {
        if (updateScheduled) {
            return;
        }

        updateScheduled = true;
        window.requestAnimationFrame(() => {
            updateScheduled = false;
            updateActiveHeading();
        });
    };

    const updateActiveHeading = () => {
        const activationOffset = 120;
        let activeHeading = headings[0];

        for (const heading of headings) {
            if (heading.getBoundingClientRect().top > activationOffset) {
                break;
            }

            activeHeading = heading;
        }

        const documentBottom = window.scrollY + window.innerHeight >= document.documentElement.scrollHeight - 2;
        if (documentBottom) {
            activeHeading = headings[headings.length - 1];
        }

        setActiveHeading(activeHeading);
    };

    function setActiveHeading(activeHeading) {
        let activeLink = null;
        links.forEach((link, heading) => {
            const isActive = heading === activeHeading;
            link.classList.toggle("active", isActive);
            if (isActive) {
                activeLink = link;
                link.setAttribute("aria-current", "location");
            } else {
                link.removeAttribute("aria-current");
            }
        });

        if (activeLink && outline.offsetParent !== null) {
            const outlineRect = outline.getBoundingClientRect();
            const linkRect = activeLink.getBoundingClientRect();
            if (linkRect.top < outlineRect.top) {
                outline.scrollTop += linkRect.top - outlineRect.top - 8;
            } else if (linkRect.bottom > outlineRect.bottom) {
                outline.scrollTop += linkRect.bottom - outlineRect.bottom + 8;
            }
        }
    }

    window.addEventListener("scroll", scheduleActiveHeadingUpdate, { passive: true });
    window.addEventListener("resize", scheduleActiveHeadingUpdate);
    updateActiveHeading();

    function createHeadingId(title, index, existingIds) {
        const normalizedTitle = title.trim()
            .toLocaleLowerCase()
            .normalize("NFKD")
            .replace(/[\u0300-\u036f]/g, "")
            .replace(/[^\p{Letter}\p{Number}]+/gu, "-")
            .replace(/^-+|-+$/g, "");
        const baseId = normalizedTitle || `section-${index + 1}`;
        let candidate = baseId;
        let suffix = 2;

        while (existingIds.has(candidate)) {
            candidate = `${baseId}-${suffix}`;
            suffix += 1;
        }

        existingIds.add(candidate);
        return candidate;
    }
})();
