<?php

declare(strict_types=1);

namespace CompTest\Network;

final class CurlTest
{
    public function __construct(private readonly string $licenseServerUrl) {}

    /** @return array<string,bool> */
    public function run(): array
    {
        if (!extension_loaded('curl')) {
            return ['curl_skipped' => true];
        }

        $r = [];

        // ── Health endpoint (GET) ──────────────────────────────────────────
        $url  = rtrim($this->licenseServerUrl, '/') . '/health';
        $ch   = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT        => 5,
            CURLOPT_CONNECTTIMEOUT => 3,
            CURLOPT_FOLLOWLOCATION => false,
        ]);
        $body   = curl_exec($ch);
        $status = curl_getinfo($ch, CURLINFO_HTTP_CODE);
        $err    = curl_error($ch);
        curl_close($ch);

        $r['health_reachable']   = $err === '' && $status === 200;
        $r['health_json_status'] = false;
        $r['health_has_db']      = false;

        if ($body !== false && $body !== '') {
            $json = json_decode($body, true);
            $r['health_json_status'] = ($json['status'] ?? '') === 'ok';
            $r['health_has_db']      = ($json['database'] ?? '') === 'ok';
        }

        // ── curl_getinfo fields ───────────────────────────────────────────
        $ch2 = curl_init($url);
        curl_setopt_array($ch2, [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT        => 5,
        ]);
        curl_exec($ch2);
        $info = curl_getinfo($ch2);
        curl_close($ch2);

        $r['curl_info_fields'] = isset($info['total_time'], $info['size_download'], $info['content_type']);

        // ── Multi-handle (parallel requests) ─────────────────────────────
        $mh    = curl_multi_init();
        $handles = [];
        for ($i = 0; $i < 3; $i++) {
            $h = curl_init($url);
            curl_setopt($h, CURLOPT_RETURNTRANSFER, true);
            curl_setopt($h, CURLOPT_TIMEOUT, 5);
            curl_multi_add_handle($mh, $h);
            $handles[] = $h;
        }
        do {
            $status = curl_multi_exec($mh, $running);
            if ($running) curl_multi_select($mh, 0.5);
        } while ($running > 0 && $status === CURLM_OK);

        $allOk = true;
        foreach ($handles as $h) {
            $code = curl_getinfo($h, CURLINFO_HTTP_CODE);
            if ($code !== 200) $allOk = false;
            curl_multi_remove_handle($mh, $h);
            curl_close($h);
        }
        curl_multi_close($mh);
        $r['curl_multi'] = $allOk;

        return $r;
    }
}
