<?php
/**
 * Hostix PHP Built-in Server Router
 * -----------------------------------
 * PHP's built-in server uses this as the "router script".
 * It routes requests to either autologin.php or adminer.php.
 *
 * Usage:  php -S 127.0.0.1:PORT router.php
 */

$uri = $_SERVER['REQUEST_URI'];
$path = parse_url($uri, PHP_URL_PATH);

if ($path === '/autologin.php') {
    require __DIR__ . '/autologin.php';
    return;
}

// All other requests (including /) → Adminer
require __DIR__ . '/adminer.php';
