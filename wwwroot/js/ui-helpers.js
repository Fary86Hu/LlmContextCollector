function selectTextInTextarea(elementId, term) {
    const textarea = document.getElementById(elementId);
    if (!textarea || !term) return;

    const text = textarea.value;
    const index = text.toLowerCase().indexOf(term.toLowerCase());

    if (index !== -1) {
        textarea.focus();
        textarea.setSelectionRange(index, index + term.length);

        // Görgetés a találathoz
        const fullHeight = textarea.scrollHeight;
        const totalChars = text.length;
        const targetPos = (index / totalChars) * fullHeight;
        
        textarea.scrollTop = targetPos - (textarea.offsetHeight / 2);
    }
}

window.scrollToBottom = (elementId) => {
    const element = document.getElementById(elementId);
    if (element) {
        element.scrollTop = element.scrollHeight;
    }
};

window.scrollToElement = (elementId) => {
    const element = document.getElementById(elementId);
    if (element) {
        element.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }
};

window.scrollToElementInContainer = (containerId, elementId) => {
    const container = document.getElementById(containerId);
    const element = document.getElementById(elementId);
    if (container && element) {
        element.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }
};

window.setTheme = (theme) => {
    if (theme === 'System') {
        const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
        document.documentElement.setAttribute('data-theme', prefersDark ? 'dark' : 'light');
    } else {
        document.documentElement.setAttribute('data-theme', theme.toLowerCase());
    }
};

window.initializePreviewInteractions = (dotNetHelper) => {
    // Globális paste figyelő (Ctrl+V)
    window.addEventListener('paste', async (e) => {
        const items = e.clipboardData.items;
        for (let i = 0; i < items.length; i++) {
            if (items[i].type.indexOf('image') !== -1) {
                const blob = items[i].getAsFile();
                const reader = new FileReader();
                reader.onload = (event) => {
                    dotNetHelper.invokeMethodAsync('OnImagePastedAsync', event.target.result);
                };
                reader.readAsDataURL(blob);
            }
        }
    });
};