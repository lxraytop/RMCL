<?php
/**
 * 从 GitHub Release 同步版本：拉取最新（或指定 tag）的版本信息、公告与 exe 资产。
 *
 * 国内服务器直连 api.github.com / objects.githubusercontent.com 经常失败，
 * 所以每个请求都允许经 config.php 里的 github_mirror（默认 gh-proxy.com）重试一次。
 * 同步只在管理员点击时发生，不影响客户端。
 */

declare(strict_types=1);

/** 用 curl 或 stream 发一个 GET，返回原始文本；失败返回 null */
function http_get_text(string $url, int $timeout = 20, array $headers = []): ?string
{
    $userAgent = 'RMCL-Update-Server/1.0';

    if (function_exists('curl_init')) {
        $ch = curl_init($url);
        $options = [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_FOLLOWLOCATION => true,
            CURLOPT_MAXREDIRS => 5,
            CURLOPT_CONNECTTIMEOUT => 10,
            CURLOPT_TIMEOUT => $timeout,
            CURLOPT_USERAGENT => $userAgent,
            CURLOPT_HTTPHEADER => $headers,
        ];
        if (defined('CURLOPT_ENCODING')) {
            $options[CURLOPT_ENCODING] = '';
        }
        curl_setopt_array($ch, $options);
        $body = curl_exec($ch);
        $code = (int) curl_getinfo($ch, CURLINFO_HTTP_CODE);
        curl_close($ch);
        if ($body === false || $code < 200 || $code >= 300) {
            return null;
        }
        return $body;
    }

    $context = stream_context_create([
        'http' => [
            'method' => 'GET',
            'timeout' => $timeout,
            'header' => implode("\r\n", array_merge(['User-Agent: ' . $userAgent], $headers)),
            'follow_location' => 1,
            'ignore_errors' => false,
        ],
    ]);
    $body = @file_get_contents($url, false, $context);
    return $body === false ? null : $body;
}

/** 把一个 URL 流式写进文件；成功返回 true */
function http_get_file(string $url, string $target, int $timeout = 300): bool
{
    $userAgent = 'RMCL-Update-Server/1.0';
    $fp = @fopen($target, 'wb');
    if ($fp === false) {
        return false;
    }

    if (function_exists('curl_init')) {
        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_FILE => $fp,
            CURLOPT_FOLLOWLOCATION => true,
            CURLOPT_MAXREDIRS => 5,
            CURLOPT_CONNECTTIMEOUT => 15,
            CURLOPT_TIMEOUT => $timeout,
            CURLOPT_USERAGENT => $userAgent,
        ]);
        $ok = curl_exec($ch);
        $code = (int) curl_getinfo($ch, CURLINFO_HTTP_CODE);
        curl_close($ch);
        fclose($fp);
        return $ok !== false && $code >= 200 && $code < 300 && filesize($target) > 0;
    }

    $context = stream_context_create([
        'http' => [
            'method' => 'GET',
            'timeout' => $timeout,
            'user_agent' => $userAgent,
            'follow_location' => 1,
        ],
    ]);
    $input = @fopen($url, 'rb', false, $context);
    if ($input === false) {
        fclose($fp);
        return false;
    }
    $bytes = stream_copy_to_stream($input, $fp);
    fclose($input);
    fclose($fp);
    return $bytes !== false && $bytes > 0;
}

/** GitHub 请求头（带 token 可选，用于提高限额或访问私有仓库） */
function gh_headers(): array
{
    $headers = [
        'Accept: application/vnd.github+json',
        'X-GitHub-Api-Version: 2022-11-28',
    ];
    $token = trim((string) cfg('github_token', ''));
    if ($token !== '') {
        $headers[] = 'Authorization: Bearer ' . $token;
    }
    return $headers;
}

function gh_mirror_url(string $url): string
{
    $mirror = trim((string) cfg('github_mirror', ''));
    if ($mirror === '') {
        return '';
    }
    return rtrim($mirror, '/') . '/' . $url;
}

/** 从资产列表里挑 exe：优先名字含 RMCL，否则取第一个 exe */
function gh_pick_asset(array $assets): ?array
{
    $exes = [];
    foreach ($assets as $asset) {
        $name = (string) ($asset['name'] ?? '');
        if (preg_match('/\.exe$/i', $name)) {
            $exes[] = [
                'name' => $name,
                'size' => (int) ($asset['size'] ?? 0),
                'url' => (string) ($asset['browser_download_url'] ?? ''),
                'digest' => (string) ($asset['digest'] ?? ''),
                'downloads' => (int) ($asset['download_count'] ?? 0),
            ];
        }
    }
    if (!$exes) {
        return null;
    }
    foreach ($exes as $exe) {
        if (stripos($exe['name'], 'RMCL') !== false) {
            return $exe;
        }
    }
    return $exes[0];
}

/**
 * 拉取一个 Release。
 * @param string $tag 留空取 latest
 * @throws RuntimeException 网络失败或仓库/tag 不存在
 */
function gh_fetch_release(string $repo, string $tag = ''): array
{
    $repo = trim($repo);
    if (!preg_match('#^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$#', $repo)) {
        throw new RuntimeException('仓库格式应为 owner/name，例如 lxraytop/RMCL');
    }

    $path = $tag === '' ? 'latest' : 'tags/' . rawurlencode($tag);
    $url = 'https://api.github.com/repos/' . $repo . '/releases/' . $path;

    $raw = http_get_text($url, 20, gh_headers());
    if ($raw === null) {
        $mirrored = gh_mirror_url($url);
        if ($mirrored !== '') {
            $raw = http_get_text($mirrored, 25, gh_headers());
        }
    }
    if ($raw === null) {
        throw new RuntimeException('无法访问 GitHub API，请检查服务器网络，或在 config.php 里配置 github_mirror');
    }

    $data = json_decode($raw, true);
    if (!is_array($data)) {
        throw new RuntimeException('GitHub 返回了无法解析的内容');
    }
    if (!empty($data['message'])) {
        throw new RuntimeException('GitHub 返回：' . $data['message'] . ($tag !== '' ? '（tag: ' . $tag . '）' : ''));
    }

    $assets = is_array($data['assets'] ?? null) ? $data['assets'] : [];
    $tagName = (string) ($data['tag_name'] ?? '');

    return [
        'repo' => $repo,
        'tag' => $tagName,
        'version' => ltrim($tagName, 'vV'),
        'title' => (string) ($data['name'] ?? ''),
        'notes' => (string) ($data['body'] ?? ''),
        'published_at' => strtotime((string) ($data['published_at'] ?? '')) ?: time(),
        'html_url' => (string) ($data['html_url'] ?? ''),
        'prerelease' => !empty($data['prerelease']),
        'asset' => gh_pick_asset($assets),
    ];
}

/**
 * 下载 Release 资产到目标路径；直连失败会用镜像重试。
 * @return array{ok:bool, url?:string, message?:string}
 */
function gh_download_asset(string $downloadUrl, string $target): array
{
    if ($downloadUrl === '') {
        return ['ok' => false, 'message' => '该 Release 没有提供可下载的 exe 资产'];
    }

    $candidates = [$downloadUrl];
    $mirrored = gh_mirror_url($downloadUrl);
    if ($mirrored !== '') {
        $candidates[] = $mirrored;
    }

    $lastError = '';
    foreach ($candidates as $index => $candidate) {
        $via = $index === 0 ? '直连 GitHub' : '镜像';
        if (@http_get_file($candidate, $target, 300)) {
            return ['ok' => true, 'url' => $candidate, 'via' => $via];
        }
        $lastError = $via . '下载失败';
        @unlink($target);
    }

    return ['ok' => false, 'message' => $lastError . '；可把文件手动放进 data/releases/ 再发布'];
}
