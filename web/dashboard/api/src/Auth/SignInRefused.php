<?php

declare(strict_types=1);

namespace TraderDash\Auth;

/** A Google sign-in that cannot be trusted; the message is the reason written to the access log. */
final class SignInRefused extends \RuntimeException
{
}
