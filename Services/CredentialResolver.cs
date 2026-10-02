namespace BryersHideoutPlugin.Services;

// Legacy local admin-token discovery was intentionally removed before public
// distribution. The plugin authenticates exclusively through the website's
// Staff login/session endpoints and never reads deployment secrets from disk.
