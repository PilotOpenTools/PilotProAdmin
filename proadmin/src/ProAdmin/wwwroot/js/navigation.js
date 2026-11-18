const navToggle = document.querySelector(".nav-toggle");

navToggle.addEventListener("click", () => {
    navToggle.setAttribute("aria-expanded", String(navToggle.getAttribute("aria-expanded") === "false"));
});

navToggle.parentElement.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && navToggle.offsetWidth && navToggle.getAttribute("aria-expanded") === "true") {
        navToggle.setAttribute("aria-expanded", "false");
        navToggle.focus();
    }
});

navToggle.hidden = false;
