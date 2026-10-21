#pragma once
#define F_GETFL 3
#define F_SETFL 4
#define O_NONBLOCK 2048
extern "C" int fcntl(int, int, ...);
