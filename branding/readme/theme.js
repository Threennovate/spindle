// ?theme=light|dark picks the version; each graphic sets its own default on <html data-theme>.
const theme = new URLSearchParams(location.search).get('theme');
if (theme) document.documentElement.dataset.theme = theme;
