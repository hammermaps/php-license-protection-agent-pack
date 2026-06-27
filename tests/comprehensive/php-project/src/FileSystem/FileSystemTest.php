<?php

declare(strict_types=1);

namespace CompTest\FileSystem;

final class FileSystemTest
{
    private string $tmpDir;

    public function __construct(string $tmpDir)
    {
        $this->tmpDir = $tmpDir . '/fs_' . getmypid();
        mkdir($this->tmpDir, 0700, true);
    }

    /** @return array<string,bool> */
    public function run(): array
    {
        $r = [];
        $base = $this->tmpDir;

        // Write + read
        $file = "$base/test.txt";
        file_put_contents($file, "line1\nline2\nline3\n");
        $r['write_read'] = file_get_contents($file) === "line1\nline2\nline3\n";

        // File size
        $r['file_size'] = filesize($file) === 18;

        // Append
        file_put_contents($file, "line4\n", FILE_APPEND);
        $lines = file($file, FILE_IGNORE_NEW_LINES);
        $r['append'] = count($lines) === 4 && $lines[3] === 'line4';

        // Copy
        $copy = "$base/copy.txt";
        copy($file, $copy);
        $r['copy'] = file_get_contents($copy) === file_get_contents($file);

        // Rename
        $renamed = "$base/renamed.txt";
        rename($copy, $renamed);
        $r['rename'] = file_exists($renamed) && !file_exists($copy);

        // Subdirectory creation
        $sub = "$base/sub/nested";
        mkdir($sub, 0700, true);
        $r['mkdir_recursive'] = is_dir($sub);

        // Write binary
        $bin = "$base/data.bin";
        $data = random_bytes(256);
        file_put_contents($bin, $data);
        $r['binary_roundtrip'] = file_get_contents($bin) === $data;

        // JSON encode/decode via file
        $json = "$base/data.json";
        $payload = ['key' => 'value', 'num' => 42, 'arr' => [1, 2, 3]];
        file_put_contents($json, json_encode($payload));
        $decoded = json_decode(file_get_contents($json), true);
        $r['json_file'] = $decoded === $payload;

        // Glob
        file_put_contents("$base/a.log", 'a');
        file_put_contents("$base/b.log", 'b');
        $logs = glob("$base/*.log");
        $r['glob'] = count($logs) === 2;

        // SplFileInfo
        $info = new \SplFileInfo($file);
        $r['spl_file_info'] = $info->isFile() && $info->getExtension() === 'txt';

        // Delete
        unlink($file);
        $r['unlink'] = !file_exists($file);

        return $r;
    }

    public function cleanup(): void
    {
        $this->rmrf($this->tmpDir);
    }

    private function rmrf(string $path): void
    {
        if (!file_exists($path)) return;
        if (is_dir($path)) {
            foreach (scandir($path) as $entry) {
                if ($entry === '.' || $entry === '..') continue;
                $this->rmrf("$path/$entry");
            }
            rmdir($path);
        } else {
            unlink($path);
        }
    }
}
