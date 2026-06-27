<?php

declare(strict_types=1);

namespace CompTest\Advanced;

final class StringTest
{
    /** @return array<string,bool> */
    public function run(): array
    {
        $r = [];

        // Multibyte string functions
        $s = 'Héllo Wörld';
        $r['mb_strlen']    = mb_strlen($s, 'UTF-8') === 11;
        $r['mb_strtolower'] = mb_strtolower('ÄRGER', 'UTF-8') === 'ärger';
        $r['mb_substr']    = mb_substr($s, 6, 5, 'UTF-8') === 'Wörld';
        $r['mb_strpos']    = mb_strpos($s, 'Wörld', 0, 'UTF-8') === 6;

        // Regex
        preg_match('/^(\w+)\s+(\w+)$/', 'Hello World', $m);
        $r['preg_match']    = ($m[1] ?? '') === 'Hello' && ($m[2] ?? '') === 'World';

        $r['preg_match_all'] = preg_match_all('/\d+/', 'a1b22c333', $m) === 3;

        $result = preg_replace('/(\w+)/', '[$1]', 'foo bar');
        $r['preg_replace']  = $result === '[foo] [bar]';

        $parts = preg_split('/[\s,]+/', 'one two,three  four');
        $r['preg_split']    = $parts === ['one', 'two', 'three', 'four'];

        // sprintf / printf formatting
        $r['sprintf_float'] = sprintf('%.2f', M_PI) === '3.14';
        $r['sprintf_pad']   = sprintf('%05d', 42) === '00042';
        $r['sprintf_named'] = vsprintf('%s=%d', ['answer', 42]) === 'answer=42';

        // String functions
        $r['str_pad']      = str_pad('hello', 10, '-', STR_PAD_BOTH) === '--hello---';
        $r['str_repeat']   = str_repeat('ab', 3) === 'ababab';
        $r['wordwrap']     = wordwrap('The quick brown fox', 10, "\n", true) === "The quick\nbrown fox";
        $r['chunk_split']  = chunk_split('ABCDEF', 2, '-') === 'AB-CD-EF-';
        $r['number_format'] = number_format(1234567.891, 2, '.', ',') === '1,234,567.89';

        // Hash functions
        $r['sha256_hex']   = strlen(hash('sha256', 'test')) === 64;
        $r['md5_32']       = strlen(md5('test')) === 32;
        $r['crc32_int']    = is_int(crc32('test'));

        // Base64
        $bin = random_bytes(32);
        $r['base64_roundtrip'] = base64_decode(base64_encode($bin)) === $bin;

        // JSON
        $obj = ['key' => 'val', 'num' => 3.14, 'arr' => [1, 2]];
        $r['json_encode_decode'] = json_decode(json_encode($obj), true) === $obj;

        $r['json_pretty'] = str_contains(
            json_encode(['a' => 1], JSON_PRETTY_PRINT),
            "\n"
        );

        // Pack / unpack (binary protocol)
        $packed = pack('NnCC', 0xDEADBEEF, 0xABCD, 0x12, 0x34);
        ['uint32' => $u32, 'uint16' => $u16] = unpack('Nuint32/nuint16', $packed);
        $r['pack_unpack'] = $u32 === 0xDEADBEEF && $u16 === 0xABCD;

        return $r;
    }
}
