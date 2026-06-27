<?php

declare(strict_types=1);

namespace CompTest\Oop\Traits;

trait Loggable
{
    private array $log = [];

    public function logEvent(string $event): void
    {
        $this->log[] = sprintf('[%s] %s', date('H:i:s'), $event);
    }

    public function getLog(): array
    {
        return $this->log;
    }
}
