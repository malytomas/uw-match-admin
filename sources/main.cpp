#include <cage-core/logger.h>

using namespace cage;

int main(int argc, const char *args[])
{
	try
	{
		Holder<Logger> conLog = newLogger();
		conLog->format.bind<logFormatConsole>();
		conLog->output.bind<logOutputStdOut>();

		// todo

		return 0;
	}
	catch (...)
	{
		detail::logCurrentCaughtException();
	}
	return 1;
}
