<?php

declare(strict_types=1);

namespace CompTest\Advanced;

final class GeneratorTest
{
    /** @return array<string,bool> */
    public function run(): array
    {
        $r = [];

        // Basic generator
        $r['basic_generator'] = iterator_to_array($this->range(1, 5)) === [1, 2, 3, 4, 5];

        // Generator with keys
        $pairs = iterator_to_array($this->indexedPairs());
        $r['generator_keys'] = $pairs === ['a' => 1, 'b' => 2, 'c' => 3];

        // Generator send
        $gen = $this->accumulator();
        $gen->current();
        $gen->send(10);
        $gen->send(20);
        $total = $gen->send(30);
        $r['generator_send'] = $total === 60;

        // Delegating generator (yield from) — use false to preserve numeric keys sequentially
        $r['yield_from'] = iterator_to_array($this->combined(), false) === [1, 2, 3, 4, 5, 6];

        // Infinite generator with early break
        $fibs = [];
        foreach ($this->fibonacci() as $n) {
            if ($n > 100) break;
            $fibs[] = $n;
        }
        $r['infinite_fibonacci'] = $fibs === [0, 1, 1, 2, 3, 5, 8, 13, 21, 34, 55, 89];

        // Generator return value
        $gen2 = $this->withReturn();
        iterator_to_array($gen2);
        $r['generator_return'] = $gen2->getReturn() === 'done';

        // Memory efficiency (count without materializing)
        $count = 0;
        foreach ($this->range(1, 10_000) as $_) {
            $count++;
        }
        $r['generator_memory_efficient'] = $count === 10_000;

        return $r;
    }

    private function range(int $start, int $end): \Generator
    {
        for ($i = $start; $i <= $end; $i++) {
            yield $i;
        }
    }

    private function indexedPairs(): \Generator
    {
        yield 'a' => 1;
        yield 'b' => 2;
        yield 'c' => 3;
    }

    private function accumulator(): \Generator
    {
        $total = 0;
        while (true) {
            $value = yield $total;
            $total += $value ?? 0;
        }
    }

    private function inner1(): \Generator { yield 1; yield 2; yield 3; }
    private function inner2(): \Generator { yield 4; yield 5; yield 6; }

    private function combined(): \Generator
    {
        yield from $this->inner1();
        yield from $this->inner2();
    }

    private function fibonacci(): \Generator
    {
        [$a, $b] = [0, 1];
        while (true) {
            yield $a;
            [$a, $b] = [$b, $a + $b];
        }
    }

    private function withReturn(): \Generator
    {
        yield 1;
        yield 2;
        return 'done';
    }
}
