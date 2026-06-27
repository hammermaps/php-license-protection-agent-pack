<?php

declare(strict_types=1);

namespace CompTest\Protection;

final class ProtectionTest
{
    /** @return array<string,bool|string> */
    public function run(string $expectedFeature = 'premium'): array
    {
        if (!extension_loaded('mmloader')) {
            return [
                'mmloader_loaded'  => 'skip: extension not loaded (plaintext mode)',
                'has_feature_fn'   => 'skip: extension not loaded',
                'feature_base'     => 'skip: extension not loaded',
                'feature_premium'  => 'skip: extension not loaded',
                'feature_bogus'    => 'skip: extension not loaded',
                'file_path_stable' => 'skip: extension not loaded',
            ];
        }

        $r = [];
        $r['mmloader_loaded'] = true;
        $r['has_feature_fn']  = function_exists('mmprotect_has_feature');

        if (function_exists('mmprotect_has_feature')) {
            $r['feature_base']    = mmprotect_has_feature('base');
            $r['feature_premium'] = mmprotect_has_feature($expectedFeature);
            $r['feature_bogus']   = !mmprotect_has_feature('__nonexistent__');
        }

        $r['file_path_stable'] = !str_contains(__FILE__, '/tmp/')
            && str_contains(__FILE__, 'ProtectionTest.php');

        return $r;
    }
}
