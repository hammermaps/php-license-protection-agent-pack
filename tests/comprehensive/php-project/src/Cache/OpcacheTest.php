<?php

declare(strict_types=1);

namespace CompTest\Cache;

final class OpcacheTest
{
    /** @return array<string,bool|int|string> */
    public function run(): array
    {
        if (!extension_loaded('Zend OPcache') && !function_exists('opcache_get_status')) {
            return ['opcache_skipped' => true];
        }

        $status = opcache_get_status(false);
        if ($status === false) {
            return ['opcache_disabled' => true];
        }

        $r = [];
        $r['opcache_enabled']       = ($status['opcache_enabled'] ?? false) === true;
        $r['has_memory_usage']      = isset($status['memory_usage']);
        $r['has_interned_strings']  = isset($status['interned_strings_usage']);
        $r['has_statistics']        = isset($status['opcache_statistics']);

        $mem = $status['memory_usage'] ?? [];
        $r['used_memory_positive']  = ($mem['used_memory'] ?? 0) > 0;
        $r['free_memory_positive']  = ($mem['free_memory'] ?? 0) > 0;

        $stats = $status['opcache_statistics'] ?? [];
        $r['hits_gte_zero']         = ($stats['hits'] ?? -1) >= 0;
        $r['misses_gte_zero']       = ($stats['misses'] ?? -1) >= 0;
        $r['cached_scripts_exists'] = array_key_exists('num_cached_scripts', $stats);

        $config = opcache_get_configuration();
        $r['config_available']      = is_array($config) && isset($config['directives']);

        return $r;
    }
}
