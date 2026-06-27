<?php

declare(strict_types=1);

require __DIR__ . '/../vendor/autoload.php';

use CompTest\Advanced\ClosureTest;
use CompTest\Advanced\GeneratorTest;
use CompTest\Advanced\StringTest;
use CompTest\Cache\ApcuTest;
use CompTest\Cache\OpcacheTest;
use CompTest\Database\DatabaseTest;
use CompTest\DateTime\DateTimeTest;
use CompTest\Exceptions\ExceptionTest;
use CompTest\Fibers\FiberTest;
use CompTest\FileSystem\FileSystemTest;
use CompTest\Math\MathTest;
use CompTest\Network\CurlTest;
use CompTest\Oop\OopTest;
use CompTest\Protection\ProtectionTest;
use CompTest\Spl\SplTest;

// ── Configuration via environment ────────────────────────────────────────────
$licenseServerUrl = getenv('MMTEST_LICENSE_SERVER') ?: 'http://127.0.0.1:5150';
$tmpDir           = getenv('MMTEST_TMP_DIR')        ?: sys_get_temp_dir();

// ── Test runner ───────────────────────────────────────────────────────────────
$suites = [];
$pass   = 0;
$fail   = 0;
$skip   = 0;

function runSuite(string $name, array $results, int &$pass, int &$fail, int &$skip): void
{
    echo "\n=== $name ===\n";
    foreach ($results as $test => $outcome) {
        if ($outcome === true) {
            echo "[PASS] $test\n";
            $pass++;
        } elseif ($outcome === false) {
            echo "[FAIL] $test\n";
            $fail++;
        } else {
            // skipped (non-bool value used as skip signal)
            echo "[SKIP] $test" . (is_string($outcome) ? ": $outcome" : '') . "\n";
            $skip++;
        }
    }
}

// ── Database ─────────────────────────────────────────────────────────────────
$dbTest = new DatabaseTest($tmpDir);
try {
    runSuite('Database (SQLite/PDO)', $dbTest->run(), $pass, $fail, $skip);
} finally {
    $dbTest->cleanup();
}

// ── File System ───────────────────────────────────────────────────────────────
$fsTest = new FileSystemTest($tmpDir);
try {
    runSuite('File System', $fsTest->run(), $pass, $fail, $skip);
} finally {
    $fsTest->cleanup();
}

// ── OOP Patterns ─────────────────────────────────────────────────────────────
runSuite('OOP (Interface, Trait, Enum, Readonly)', (new OopTest())->run(), $pass, $fail, $skip);

// ── Closures ─────────────────────────────────────────────────────────────────
runSuite('Closures & Functional', (new ClosureTest())->run(), $pass, $fail, $skip);

// ── Generators ───────────────────────────────────────────────────────────────
runSuite('Generators', (new GeneratorTest())->run(), $pass, $fail, $skip);

// ── Strings ──────────────────────────────────────────────────────────────────
runSuite('String & Binary', (new StringTest())->run(), $pass, $fail, $skip);

// ── DateTime ─────────────────────────────────────────────────────────────────
runSuite('DateTime & Timezones', (new DateTimeTest())->run(), $pass, $fail, $skip);

// ── Math ─────────────────────────────────────────────────────────────────────
runSuite('Math & Precision', (new MathTest())->run(), $pass, $fail, $skip);

// ── Exceptions ───────────────────────────────────────────────────────────────
runSuite('Exceptions & Errors', (new ExceptionTest())->run(), $pass, $fail, $skip);

// ── SPL Data Structures ──────────────────────────────────────────────────────
runSuite('SPL Data Structures', (new SplTest())->run(), $pass, $fail, $skip);

// ── Fibers ───────────────────────────────────────────────────────────────────
runSuite('Fibers (PHP 8.1+)', (new FiberTest())->run(), $pass, $fail, $skip);

// ── APCu Cache ───────────────────────────────────────────────────────────────
runSuite('APCu Cache', (new ApcuTest())->run(), $pass, $fail, $skip);

// ── OPcache ──────────────────────────────────────────────────────────────────
runSuite('OPcache Stats', (new OpcacheTest())->run(), $pass, $fail, $skip);

// ── Network / cURL ───────────────────────────────────────────────────────────
runSuite('Network (cURL)', (new CurlTest($licenseServerUrl))->run(), $pass, $fail, $skip);

// ── MMProtect Protection ─────────────────────────────────────────────────────
runSuite('MMProtect Protection', (new ProtectionTest())->run(), $pass, $fail, $skip);

// ── Summary ──────────────────────────────────────────────────────────────────
echo "\n";
echo str_repeat('=', 50) . "\n";
echo "MMPROTECT_COMPREHENSIVE_TEST_RESULT\n";
printf("PASS=%d FAIL=%d SKIP=%d TOTAL=%d\n", $pass, $fail, $skip, $pass + $fail + $skip);
echo ($fail === 0 ? "STATUS=OK\n" : "STATUS=FAILED\n");
echo str_repeat('=', 50) . "\n";
