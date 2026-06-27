<?php

declare(strict_types=1);

namespace CompTest\Advanced;

final class ClosureTest
{
    /** @return array<string,bool> */
    public function run(): array
    {
        $r = [];

        // array_map + arrow function
        $doubled = array_map(fn(int $x) => $x * 2, [1, 2, 3, 4, 5]);
        $r['array_map_arrow'] = $doubled === [2, 4, 6, 8, 10];

        // array_filter
        $evens = array_values(array_filter([1, 2, 3, 4, 5, 6], fn($x) => $x % 2 === 0));
        $r['array_filter'] = $evens === [2, 4, 6];

        // array_reduce
        $sum = array_reduce([1, 2, 3, 4, 5], fn($carry, $item) => $carry + $item, 0);
        $r['array_reduce'] = $sum === 15;

        // usort
        $words = ['banana', 'apple', 'cherry', 'date'];
        usort($words, fn($a, $b) => strlen($a) <=> strlen($b));
        $r['usort'] = $words[0] === 'date' && $words[1] === 'apple';

        // Closure bind
        $obj = new class { private int $x = 42; };
        $fn  = \Closure::bind(fn() => $this->x, $obj, $obj::class);
        $r['closure_bind'] = $fn() === 42;

        // Partial application via closure
        $multiply = fn(int $factor) => fn(int $x) => $x * $factor;
        $triple   = $multiply(3);
        $r['partial_application'] = $triple(7) === 21;

        // Memoization via closure + static
        $calls = 0;
        $memo  = function(int $n) use (&$calls): int {
            static $cache = [];
            if (!isset($cache[$n])) {
                $calls++;
                $cache[$n] = $n * $n;
            }
            return $cache[$n];
        };
        $memo(5); $memo(5); $memo(6);
        $r['memoize'] = $calls === 2;

        // Recursive closure via use-by-ref
        $fib = null;
        $fib = function(int $n) use (&$fib): int {
            return $n <= 1 ? $n : $fib($n - 1) + $fib($n - 2);
        };
        $r['recursive_closure'] = $fib(10) === 55;

        // array_walk
        $data = ['a' => 1, 'b' => 2, 'c' => 3];
        array_walk($data, function(&$val, $key) { $val = "$key:$val"; });
        $r['array_walk'] = $data === ['a' => 'a:1', 'b' => 'b:2', 'c' => 'c:3'];

        // Pipe via array_reduce
        $pipe = fn(mixed $val, array $fns) =>
            array_reduce($fns, fn($carry, $fn) => $fn($carry), $val);
        $result = $pipe(
            '  Hello World  ',
            [fn($s) => trim($s), fn($s) => strtolower($s), fn($s) => str_replace(' ', '_', $s)]
        );
        $r['pipe'] = $result === 'hello_world';

        return $r;
    }
}
