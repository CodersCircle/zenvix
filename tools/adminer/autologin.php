<?php
/**
 * Hostix Autologin Bridge for Adminer
 * ------------------------------------
 * Adminer ignores password= in GET query strings for security.
 * This page accepts credentials via GET, then immediately auto-submits
 * a hidden POST form to Adminer's auth endpoint.
 *
 * Adminer auth POST fields:
 *   auth[driver]    = server | pgsql | sqlite | mongo
 *   auth[server]    = host:port
 *   auth[username]  = root
 *   auth[password]  = (the actual password)
 *   auth[db]        = database name
 *   auth[permanent] = 1 (ticks "Permanent login")
 */

$driver   = htmlspecialchars($_GET['driver']   ?? 'server');
$server   = htmlspecialchars($_GET['server']   ?? '127.0.0.1:3306');
$username = htmlspecialchars($_GET['username'] ?? 'root');
$password = htmlspecialchars($_GET['password'] ?? 'password123');
$db       = htmlspecialchars($_GET['db']       ?? 'test');
?><!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Connecting — Hostix Database Panel</title>
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body {
    background: #0f0f17;
    color: #a0a0b8;
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    min-height: 100vh;
    gap: 16px;
  }
  .spinner {
    width: 36px; height: 36px;
    border: 3px solid rgba(139,92,246,0.2);
    border-top-color: #8b5cf6;
    border-radius: 50%;
    animation: spin 0.7s linear infinite;
  }
  @keyframes spin { to { transform: rotate(360deg); } }
  p { font-size: 14px; opacity: 0.6; }
</style>
</head>
<body>
  <div class="spinner"></div>
  <p>Opening database panel&hellip;</p>

  <!--
    Hidden form that auto-submits via POST to Adminer.
    Adminer's login endpoint accepts auth[] fields via POST.
    This is the only way to pass a password + tick Permanent login.
  -->
  <form id="hostix_autologin" method="post" action="/">
    <input type="hidden" name="auth[driver]"    value="<?= $driver ?>">
    <input type="hidden" name="auth[server]"    value="<?= $server ?>">
    <input type="hidden" name="auth[username]"  value="<?= $username ?>">
    <input type="hidden" name="auth[password]"  value="<?= $password ?>">
    <input type="hidden" name="auth[db]"        value="<?= $db ?>">
    <input type="hidden" name="auth[permanent]" value="1">
  </form>

  <script>
    // Auto-submit immediately — the spinner shows briefly while this happens
    document.getElementById('hostix_autologin').submit();
  </script>
</body>
</html>
