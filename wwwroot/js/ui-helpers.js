(function () {
    function applyTheme(theme) {
        if (theme === 'Dark' || (theme === 'System' && window.matchMedia('(prefers-color-scheme: dark)').matches)) {
            document.documentElement.setAttribute('data-theme', 'dark');
        } else {
            document.documentElement.removeAttribute('data-theme');
        }
    }
    window.setTheme = (theme) => {
        localStorage.setItem('theme', theme);
        applyTheme(theme);
    };
    const savedTheme = localStorage.getItem('theme') || 'System';
    applyTheme(savedTheme);

    const mediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
    mediaQuery.addEventListener('change', () => {
        const currentTheme = localStorage.getItem('theme') || 'System';
        if (currentTheme === 'System') {
            applyTheme('System');
        }
    });
})();

function scrollToElement(id) {
    const element = document.getElementById(id);
    if (element) {
        element.scrollIntoView({ block: 'nearest' });
    }
}

function scrollToElementInContainer(containerId, elementId) {
    const element = document.getElementById(elementId);
    if (element) {
        element.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }
}

function scrollToBottom(elementId) {
    const element = document.getElementById(elementId);
    if (element) {
        element.scrollTop = element.scrollHeight;
    }
}

window.initializePreviewInteractions = (dotNetObj) => {
    const box = document.getElementById('preview-box');
    if (box) {
        box.addEventListener('click', (e) => {
            const target = e.target.closest('.ref-badge');
            if (target && target.dataset.typeName) {
                e.preventDefault();
                e.stopPropagation();
                dotNetObj.invokeMethodAsync('OnReferenceClicked', target.dataset.typeName);
            }
        });
    }

    window.addEventListener('keydown', (e) => {
        if (e.ctrlKey && e.key.toLowerCase() === 'b') {
            e.preventDefault();
            dotNetObj.invokeMethodAsync('OnCtrlB_Pressed');
        }
    });

    window.addEventListener('paste', (e) => {
        const items = (e.clipboardData || window.clipboardData).items;
        for (let i = 0; i < items.length; i++) {
            if (items[i].type.indexOf('image') !== -1) {
                const file = items[i].getAsFile();
                const reader = new FileReader();
                reader.onload = function (event) {
                    dotNetObj.invokeMethodAsync('OnImagePastedAsync', event.target.result);
                };
                reader.readAsDataURL(file);
            }
        }
    });
};