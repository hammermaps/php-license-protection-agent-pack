<?php

declare(strict_types=1);

namespace CompTest\Cache;

final class ApcuTest
{
    /** @return array<string,bool> */
    public function run(): array
    {
        if (!extension_loaded('apcu') || !ini_get('apc.enable_cli')) {
            return ['apcu_skipped' => true];
        }

        $r = [];
        $ns = 'comptest_' . getmypid() . '_';

        // Store scalar
        apcu_store($ns . 'str', 'hello');
        $r['store_string'] = apcu_fetch($ns . 'str') === 'hello';

        // Store array
        apcu_store($ns . 'arr', [1, 2, 3]);
        $r['store_array'] = apcu_fetch($ns . 'arr') === [1, 2, 3];

        // Store integer + TTL (generous TTL, just tests mechanics)
        apcu_store($ns . 'int', 42, 60);
        $r['store_int_ttl'] = apcu_fetch($ns . 'int') === 42;

        // Exists
        $r['exists'] = apcu_exists($ns . 'str');

        // Increment / Decrement
        apcu_store($ns . 'counter', 10);
        apcu_inc($ns . 'counter', 5);
        apcu_dec($ns . 'counter', 3);
        $r['inc_dec'] = apcu_fetch($ns . 'counter') === 12;

        // Delete
        apcu_delete($ns . 'str');
        $r['delete'] = !apcu_exists($ns . 'str');

        // Add (only if not exists)
        apcu_store($ns . 'once', 'first');
        apcu_add($ns . 'once', 'second');
        $r['add_no_overwrite'] = apcu_fetch($ns . 'once') === 'first';

        // Cas (compare-and-swap): apcu_cas(key, old_int, new_int)
        apcu_store($ns . 'cas', 100);
        $r['cas'] = apcu_cas($ns . 'cas', 100, 200) && apcu_fetch($ns . 'cas') === 200;

        // Cache info (keys count)
        $info = apcu_cache_info();
        $r['cache_info'] = is_array($info) && isset($info['num_entries']);

        // Cleanup
        apcu_delete($ns . 'arr');
        apcu_delete($ns . 'int');
        apcu_delete($ns . 'counter');
        apcu_delete($ns . 'once');
        apcu_delete($ns . 'cas');

        return $r;
    }
}
