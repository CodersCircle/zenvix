// Force scroll to top (Homepage) on reload/refresh
if ('scrollRestoration' in history) {
    history.scrollRestoration = 'manual';
}
window.scrollTo(0, 0);

lucide.createIcons();

// 1. Sidebar Tab Switching
const sidebarItems = document.querySelectorAll('.sidebar-item');
const mockupPages = document.querySelectorAll('.mockup-page');

sidebarItems.forEach(item => {
    item.addEventListener('click', () => {
        // Remove active class from all items
        sidebarItems.forEach(i => i.classList.remove('active'));
        // Add active class to clicked item
        item.classList.add('active');

        // Get target page ID
        const target = item.getAttribute('data-target');

        // Hide all pages
        mockupPages.forEach(page => {
            page.style.display = 'none';
            page.classList.remove('active');
        });

        // Show target page
        const activePage = document.getElementById('page-' + target);
        if (activePage) {
            activePage.style.display = 'block';
            // Re-render lucide icons inside page if any
            lucide.createIcons();
        }
    });
});

// 2. Download Button Spinner Logic
const downloadButtons = [
    document.getElementById('nav-download-btn'),
    document.getElementById('hero-download-btn'),
    document.getElementById('footer-download-btn')
];

downloadButtons.forEach(btn => {
    if (!btn) return;
    btn.addEventListener('click', function (e) {
        // Prevent immediate download
        e.preventDefault();
        const downloadUrl = this.getAttribute('href');

        if (this.classList.contains('loading')) return;
        this.classList.add('loading');

        // Store original HTML
        const originalHtml = this.innerHTML;

        // Show Spinner inside button
        this.innerHTML = `<svg class="spinner" viewBox="0 0 50 50" style="width:18px; height:18px; fill:none; stroke:#fff; stroke-width:5; stroke-linecap:round; display:inline-block; vertical-align:middle; margin-right:8px;"><circle cx="25" cy="25" r="20" stroke="rgba(255,255,255,0.2)" fill="none"/><circle cx="25" cy="25" r="20" stroke="#fff" stroke-dasharray="80, 200" stroke-dashoffset="0" fill="none"/></svg> Starting Download...`;

        setTimeout(() => {
            // Create dynamic download action
            const tempLink = document.createElement('a');
            tempLink.href = downloadUrl;
            tempLink.setAttribute('download', '');
            document.body.appendChild(tempLink);
            tempLink.click();
            document.body.removeChild(tempLink);

            // Revert UI after some extra time to let download start
            setTimeout(() => {
                this.innerHTML = originalHtml;
                this.classList.remove('loading');
                lucide.createIcons();
            }, 1200);
        }, 1000);
    });
});

// 3. Services & Dashboard Engine Toggle Button UI/UX Toggling & Toast Notification
const serviceToggleBtns = document.querySelectorAll('#page-services .toggle-btn, #page-dashboard .toggle-btn');
const toast = document.getElementById('mockup-toast');
const toastTitle = document.getElementById('toast-title');
const toastMsg = document.getElementById('toast-msg');
const toastIconBox = document.getElementById('toast-icon-box');
let toastTimeout = null;

serviceToggleBtns.forEach(btn => {
    btn.addEventListener('click', function () {
        const listItem = this.closest('.list-item.mini') || this.closest('.dashboard-card');
        const serviceName = listItem.querySelector('.item-title, h4').innerText;
        const statusBadge = listItem.querySelector('.badge');

        // Toggle active state
        const isActive = this.classList.toggle('active');

        // Update badge and trigger Toast
        if (isActive) {
            statusBadge.innerText = 'Running';
            statusBadge.className = 'badge badge-success';
            showToast('Engine Started', `${serviceName} has launched successfully!`, 'success');
        } else {
            statusBadge.innerText = 'Stopped';
            statusBadge.className = 'badge badge-muted';
            showToast('Engine Stopped', `${serviceName} has been powered off.`, 'warning');
        }
    });
});

function showToast(title, message, type) {
    // Clear existing timeout
    if (toastTimeout) {
        clearTimeout(toastTimeout);
    }

    // Set content
    toastTitle.innerText = title;
    toastMsg.innerText = message;

    // Set styles and icons based on type
    if (type === 'success') {
        toast.className = 'mockup-toast show success';
        toastIconBox.innerHTML = '<i data-lucide="check-circle"></i>';
    } else {
        toast.className = 'mockup-toast show warning';
        toastIconBox.innerHTML = '<i data-lucide="alert-circle"></i>';
    }

    // Refresh lucide icon
    lucide.createIcons();

    // Auto-hide after 2 seconds (2000ms)
    toastTimeout = setTimeout(() => {
        toast.classList.remove('show');
    }, 2000);
}

// 4. Browser Preview Popup Modal Interactivity
const viewSiteBtns = document.querySelectorAll('.btn-view-site');
const browserModal = document.getElementById('browser-modal');
const browserViewport = document.getElementById('browser-viewport-container');
const modalCloseBtn = document.getElementById('modal-close-btn');
const modalBrowserBack = document.getElementById('modal-browser-back');
const modalBrowserUrlText = document.getElementById('modal-browser-url-text');
const browserModalTitle = document.getElementById('browser-modal-title');

async function loadDashboardPreview(site) {
    try {
        // Fetch the separate standalone HTML dashboard component if not loaded
        if (!browserViewport.querySelector('.m3-dashboard')) {
            const response = await fetch('components/dashboard-preview.html');
            if (response.ok) {
                browserViewport.innerHTML = await response.text();
            } else {
                console.error("Failed to fetch dashboard component:", response.statusText);
            }
        }

        // Update contents dynamically based on the site clicked!
        const m3SiteTitle = document.getElementById('m3-site-title');
        const m3SiteSubtitle = document.getElementById('m3-site-subtitle');
        const m3Val1 = document.getElementById('m3-val-1');
        const m3Val2 = document.getElementById('m3-val-2');
        const m3Val3 = document.getElementById('m3-val-3');

        if (site === 'react-dashboard.test') {
            modalBrowserUrlText.innerText = 'https://react-dashboard.test/dashboard';
            browserModalTitle.innerText = 'react-dashboard.test - Zenvix Browser Preview';
            if (m3SiteTitle) m3SiteTitle.innerText = 'Material 3 Studio';
            if (m3SiteSubtitle) m3SiteSubtitle.innerText = 'Production Environment Node.js App';
            if (m3Val1) m3Val1.innerText = '$24,890';
            if (m3Val2) m3Val2.innerText = '1,402 /s';
            if (m3Val3) m3Val3.innerText = '184 MB';
        } else if (site === 'laravel-blog.test') {
            modalBrowserUrlText.innerText = 'https://laravel-blog.test/admin';
            browserModalTitle.innerText = 'laravel-blog.test - Zenvix Browser Preview';
            if (m3SiteTitle) m3SiteTitle.innerText = 'Laravel Control Panel';
            if (m3SiteSubtitle) m3SiteSubtitle.innerText = 'Artisan Engine v10.4';
            if (m3Val1) m3Val1.innerText = '8,412 Views';
            if (m3Val2) m3Val2.innerText = '42 Active';
            if (m3Val3) m3Val3.innerText = '64 MB';
        } else {
            modalBrowserUrlText.innerText = 'https://wordpress-store.test/dashboard';
            browserModalTitle.innerText = 'wordpress-store.test - Zenvix Browser Preview';
            if (m3SiteTitle) m3SiteTitle.innerText = 'WordPress Storefront';
            if (m3SiteSubtitle) m3SiteSubtitle.innerText = 'WooCommerce Cloud Engine';
            if (m3Val1) m3Val1.innerText = '14 Orders';
            if (m3Val2) m3Val2.innerText = '19.2k Visits';
            if (m3Val3) m3Val3.innerText = '112 MB';
        }

        lucide.createIcons();
    } catch (err) {
        console.error("Network error loading dashboard preview component:", err);
    }
}

viewSiteBtns.forEach(btn => {
    btn.addEventListener('click', function (e) {
        e.stopPropagation();
        const site = this.getAttribute('data-site');

        // Open browser modal popup
        browserModal.classList.add('show');

        // Trigger dynamic component loading
        loadDashboardPreview(site);
    });
});

// Close modal when close button, back button, or backdrop overlay is clicked
[modalCloseBtn, modalBrowserBack].forEach(btn => {
    if (btn) {
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            browserModal.classList.remove('show');
        });
    }
});

// Close on backdrop overlay click
browserModal.addEventListener('click', (e) => {
    if (e.target === browserModal) {
        browserModal.classList.remove('show');
    }
});

// 5. Developer Quote Popup Modal Logic
const mainWindowClose = document.getElementById('main-window-close');
const quoteModal = document.getElementById('quote-modal');
const quoteCloseBtn = document.getElementById('quote-close-btn');
const quoteText = document.getElementById('quote-text');

const developerQuotes = [
    '"Code is like humor. When you have to explain it, it’s bad.<br>Build so well that the world can\'t ignore your creations."',
    '"Code likho aisa jo shor machaye,<br>Mehnat itni karo ki har system chal jaye."',
    '"Every great software starts with a single line of code.<br>Keep writing, keep building, keep dreaming."',
    '"Bug se darna nahi, unhe harana seekho,<br>Ek behtareen developer ban kar duniya ko dikhao."',
    '"The best way to predict the future is to invent it.<br>Write the future, one function at a time."',
    '"Mushkilein toh aayengi coding ke is safar mein,<br>Par jo rukta nahi, wahi kamaal karta hai."',
    '"First, solve the problem. Then, write the code.<br>Your logic is your superpower."',
    '"Sirf software nahi, ek vishwas banao,<br>Har line mein apni lagan aur kshamta dikhao."',
    '"Make it work, make it right, make it fast.<br>You are a digital architect building the tomorrow."',
    '"Keyboard ki har ek hit mein ek kahani hai,<br>Tu ruk mat, teri mehnat hi sabse badi nishani hai."',
    '"Software is a great combination of artistry and engineering.<br>Paint your digital masterpiece with pride."',
    '"Har ek compiler error ek naya sabak hai,<br>Tumhare andar ka developer sabse alag hai."',
    '"Great developers are not born. They are made through sleepless nights,<br>relentless debugging, and an unyielding passion."',
    '"Jo sapne tumne dekhe hain unhe code mein dhalo,<br>Mehnat ki is aag mein thoda aur khud ko jhalo."'
];

if (mainWindowClose) {
    mainWindowClose.addEventListener('click', (e) => {
        e.preventDefault();
        e.stopPropagation();

        // Get random quote
        const randomIndex = Math.floor(Math.random() * developerQuotes.length);
        quoteText.innerHTML = developerQuotes[randomIndex];

        // Show quote modal
        quoteModal.classList.add('show');
        lucide.createIcons();
    });
}

if (quoteCloseBtn) {
    quoteCloseBtn.addEventListener('click', (e) => {
        e.stopPropagation();
        quoteModal.classList.remove('show');
    });
}

quoteModal.addEventListener('click', (e) => {
    if (e.target === quoteModal) {
        quoteModal.classList.remove('show');
    }
});

// 6. Live GitHub Release Download Counter
async function updateDownloadCounter() {
    const counterElement = document.getElementById('download-counter');
    if (!counterElement) return;

    // Premium fallback baseline
    let baseDownloads = 437;

    // Fetch from local storage if clicked before to keep increments persistent
    const storedClicks = localStorage.getItem('zenvix_extra_downloads');
    if (storedClicks) {
        baseDownloads += parseInt(storedClicks, 10);
    }

    counterElement.innerText = baseDownloads.toLocaleString();

    try {
        // Fetch actual release details from GitHub API
        const response = await fetch('https://api.github.com/repos/CodersCircle/zenvix/releases');
        if (response.ok) {
            const releases = await response.json();
            let apiDownloads = 0;

            // Sum up all asset download counts across all releases
            releases.forEach(release => {
                if (release.assets) {
                    release.assets.forEach(asset => {
                        apiDownloads += asset.download_count || 0;
                    });
                }
            });

            // If we have actual downloads recorded on GitHub releases, use that or sum with baseline!
            if (apiDownloads > 0) {
                const total = baseDownloads + apiDownloads;
                counterElement.innerText = total.toLocaleString();
            }
        }
    } catch (err) {
        console.log("GitHub API limits or network issues, loaded premium baseline.");
    }
}

// Increment counter on click to simulate real-time download tracking
function registerDownloadClick() {
    let extra = parseInt(localStorage.getItem('zenvix_extra_downloads') || '0', 10);
    extra += 1;
    localStorage.setItem('zenvix_extra_downloads', extra);
    updateDownloadCounter();
}

// Attach trigger to all download buttons
document.querySelectorAll('a[download]').forEach(btn => {
    btn.addEventListener('click', () => {
        // Delay slightly to match spinner animation start
        setTimeout(registerDownloadClick, 800);
    });
});

// Initialize counter
updateDownloadCounter();

// 7. Premium Custom Legal Modals Interaction
const legalModal = document.getElementById('legal-modal');
const legalTitle = document.getElementById('legal-modal-title');
const legalContent = document.getElementById('legal-modal-content');
const legalCloseBtn = document.getElementById('legal-modal-close-btn');

const legalTitles = {
    privacy: "Privacy Policy",
    terms: "Terms of Service",
    agreement: "User Agreement"
};

document.querySelectorAll('.legal-link').forEach(link => {
    link.addEventListener('click', async (e) => {
        e.preventDefault();
        e.stopPropagation();

        const docType = link.getAttribute('data-doc');
        const title = legalTitles[docType] || "Legal Document";

        try {
            // Fetch the separate standalone HTML document contents asynchronously
            const response = await fetch(`legal/${docType}.html`);
            if (response.ok) {
                const htmlContent = await response.text();
                legalTitle.innerText = title;
                legalContent.innerHTML = htmlContent;
                legalModal.classList.add('show');
            } else {
                console.error("Failed to load legal document content:", response.statusText);
            }
        } catch (err) {
            console.error("Network error fetching legal document:", err);
        }
    });
});

if (legalCloseBtn) {
    legalCloseBtn.addEventListener('click', (e) => {
        e.stopPropagation();
        legalModal.classList.remove('show');
    });
}

legalModal.addEventListener('click', (e) => {
    if (e.target === legalModal) {
        legalModal.classList.remove('show');
    }
});

// 4. Dynamic Download Link Updater
async function updateDownloadLinks() {
    try {
        const response = await fetch("https://api.github.com/repos/CodersCircle/zenvix/contents/versions");
        if (!response.ok) return;
        const files = await response.json();
        const setupFiles = files.filter(f => f.name.startsWith("Zenvix-Setup-V") && f.name.endsWith(".exe"));
        if (setupFiles.length > 0) {
            setupFiles.sort((a, b) => b.name.localeCompare(a.name));
            const latestFile = setupFiles[0];
            const downloadUrl = `https://github.com/CodersCircle/zenvix/raw/main/${latestFile.name}`;
            
            // Update all download buttons
            const elements = document.querySelectorAll("[download]");
            elements.forEach(el => {
                el.href = downloadUrl;
                if (el.id === "nav-download-btn") {
                    let versionTag = latestFile.name.replace("Zenvix-Setup-", "").replace(".exe", "");
                    const vMatch = versionTag.match(/V(\d)(\d)(\d)/);
                    if (vMatch) versionTag = `v${vMatch[1]}.${vMatch[2]}.${vMatch[3]}`;
                    el.innerHTML = `<i data-lucide="download"></i> Download ${versionTag}`;
                }
            });
            if (window.lucide) window.lucide.createIcons();
        }
    } catch (err) {
        console.error("Error updating download links:", err);
    }
}

document.addEventListener("DOMContentLoaded", updateDownloadLinks);
updateDownloadLinks();
