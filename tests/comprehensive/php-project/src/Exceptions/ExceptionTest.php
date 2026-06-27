<?php

declare(strict_types=1);

namespace CompTest\Exceptions;

final class ExceptionTest
{
    /** @return array<string,bool> */
    public function run(): array
    {
        $r = [];

        // Basic try/catch
        try {
            throw new \RuntimeException('test', 42);
        } catch (\RuntimeException $e) {
            $r['basic_catch']     = $e->getMessage() === 'test';
            $r['exception_code']  = $e->getCode() === 42;
        }

        // Exception chaining
        try {
            try {
                throw new \InvalidArgumentException('inner');
            } catch (\InvalidArgumentException $prev) {
                throw new \RuntimeException('outer', 0, $prev);
            }
        } catch (\RuntimeException $e) {
            $r['chain_message']   = $e->getMessage() === 'outer';
            $r['chain_previous']  = $e->getPrevious()?->getMessage() === 'inner';
        }

        // Multiple catch types
        $caught = 'none';
        try {
            throw new \OverflowException('overflow');
        } catch (\UnderflowException) {
            $caught = 'underflow';
        } catch (\OverflowException) {
            $caught = 'overflow';
        } catch (\RuntimeException) {
            $caught = 'runtime';
        }
        $r['multi_catch_order'] = $caught === 'overflow';

        // finally always runs
        $finallyRan = false;
        $result = null;
        try {
            $result = 'try';
            throw new \LogicException('x');
        } catch (\LogicException) {
            $result = 'catch';
        } finally {
            $finallyRan = true;
        }
        $r['finally_runs']  = $finallyRan;
        $r['finally_after'] = $result === 'catch';

        // finally with return
        $r['finally_with_return'] = $this->finallyReturn() === 'finally_wins';

        // Custom exception hierarchy
        try {
            throw new AppException('app error', AppErrorCode::NotFound);
        } catch (AppException $e) {
            $r['custom_exception']  = $e->getMessage() === 'app error';
            $r['custom_code_enum']  = $e->errorCode === AppErrorCode::NotFound;
        }

        // Catch Throwable (catches both Error and Exception)
        try {
            throw new \TypeError('type mismatch');
        } catch (\Throwable $t) {
            $r['catch_throwable']   = $t instanceof \TypeError;
        }

        // Error (not Exception)
        try {
            $arr = [];
            /** @phpstan-ignore-next-line */
            $_ = $arr['key'] ?? throw new \RuntimeException('missing');
        } catch (\RuntimeException $e) {
            $r['null_coalesce_throw'] = $e->getMessage() === 'missing';
        }

        // Union catch (PHP 8.0+)
        $caught2 = false;
        try {
            throw new \DomainException('domain');
        } catch (\InvalidArgumentException | \DomainException) {
            $caught2 = true;
        }
        $r['union_catch'] = $caught2;

        // set_error_handler
        $errorCaught = false;
        set_error_handler(function(int $errno, string $errstr) use (&$errorCaught): bool {
            $errorCaught = ($errstr === 'test_error_trigger');
            return true;
        });
        trigger_error('test_error_trigger', E_USER_NOTICE);
        restore_error_handler();
        $r['error_handler'] = $errorCaught;

        // Stack trace
        try {
            $this->deep1();
        } catch (\Exception $e) {
            $trace = $e->getTrace();
            $r['stack_trace']     = count($trace) >= 3;
            $r['trace_has_file']  = isset($trace[0]['file']);
            $r['trace_has_line']  = isset($trace[0]['line']);
        }

        return $r;
    }

    private function finallyReturn(): string
    {
        try {
            return 'try_result';
        } finally {
            return 'finally_wins';
        }
    }

    private function deep1(): never { $this->deep2(); }
    private function deep2(): never { $this->deep3(); }
    private function deep3(): never { throw new \Exception('deep'); }
}

class AppException extends \RuntimeException
{
    public function __construct(string $message, public readonly AppErrorCode $errorCode)
    {
        parent::__construct($message);
    }
}

enum AppErrorCode
{
    case NotFound;
    case Unauthorized;
    case ServerError;
}
