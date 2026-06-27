<?php

declare(strict_types=1);

namespace CompTest\Spl;

final class SplTest
{
    /** @return array<string,bool> */
    public function run(): array
    {
        $r = [];

        // SplStack (LIFO)
        $stack = new \SplStack();
        $stack->push('a');
        $stack->push('b');
        $stack->push('c');
        $r['stack_count']   = count($stack) === 3;
        $r['stack_top']     = $stack->top() === 'c';
        $r['stack_pop']     = $stack->pop() === 'c';
        $r['stack_pop2']    = $stack->pop() === 'b';
        $r['stack_not_empty'] = !$stack->isEmpty();

        // SplQueue (FIFO)
        $queue = new \SplQueue();
        $queue->enqueue('first');
        $queue->enqueue('second');
        $queue->enqueue('third');
        $r['queue_count']   = count($queue) === 3;
        $r['queue_dequeue'] = $queue->dequeue() === 'first';
        $r['queue_bottom']  = $queue->bottom() === 'second';

        // SplMinHeap
        $minH = new \SplMinHeap();
        foreach ([5, 2, 8, 1, 9, 3] as $v) {
            $minH->insert($v);
        }
        $extracted = [];
        while (!$minH->isEmpty()) {
            $extracted[] = $minH->extract();
        }
        $r['min_heap_sorted'] = $extracted === [1, 2, 3, 5, 8, 9];

        // SplMaxHeap
        $maxH = new \SplMaxHeap();
        foreach ([5, 2, 8, 1] as $v) {
            $maxH->insert($v);
        }
        $r['max_heap_top']  = $maxH->extract() === 8;

        // SplFixedArray
        $fixed = new \SplFixedArray(5);
        for ($i = 0; $i < 5; $i++) {
            $fixed[$i] = $i * $i;
        }
        $r['fixed_count']   = count($fixed) === 5;
        $r['fixed_values']  = $fixed[4] === 16;
        $r['fixed_toarray'] = \SplFixedArray::fromArray([1, 2, 3])->toArray() === [1, 2, 3];

        // SplDoublyLinkedList
        $dll = new \SplDoublyLinkedList();
        $dll->push('x');
        $dll->push('y');
        $dll->unshift('w');
        $r['dll_count']     = count($dll) === 3;
        $r['dll_bottom']    = $dll->bottom() === 'w';
        $r['dll_top']       = $dll->top() === 'y';

        // ArrayObject
        $ao = new \ArrayObject(['a' => 1, 'b' => 2, 'c' => 3]);
        $r['ao_count']      = count($ao) === 3;
        $r['ao_offset_get'] = $ao['b'] === 2;
        $r['ao_offset_set'] = (function() use ($ao): bool {
            $ao['d'] = 4;
            return $ao->offsetGet('d') === 4;
        })();
        $r['ao_offset_exists'] = $ao->offsetExists('a');
        $ao->offsetUnset('a');
        $r['ao_offset_unset']  = !$ao->offsetExists('a');
        $r['ao_append']     = (function() use ($ao): bool {
            $ao->append(99);
            return $ao->count() === 4;  // b, c, d, 99
        })();

        // SplPriorityQueue
        $pq = new \SplPriorityQueue();
        $pq->insert('low',    1);
        $pq->insert('high',   10);
        $pq->insert('medium', 5);
        $r['pq_highest_first'] = $pq->extract() === 'high';
        $r['pq_second']        = $pq->extract() === 'medium';

        // SplObjectStorage
        $os = new \SplObjectStorage();
        $obj1 = new \stdClass();
        $obj2 = new \stdClass();
        $os->attach($obj1, 'data1');
        $os->attach($obj2, 'data2');
        $r['os_count']      = $os->count() === 2;
        $r['os_contains']   = $os->contains($obj1);
        $os->detach($obj1);
        $r['os_detach']     = !$os->contains($obj1);

        return $r;
    }
}
