// SteeringWheelSDKIndependant.cpp : Defines the entry point for the console application.
//

#include "stdafx.h"

// Direct Input
#pragma comment(lib, "dinput8.lib")
#pragma comment(lib, "dxguid.lib")
#include <dinput.h>

// Logitech Wheel SDK
#pragma comment(lib, "LogitechSteeringWheelLib.lib")
#include "LogitechSteeringWheelLib.h"

// Logitech Wheel No SDK
#include "LogiIndependant.h"

LPDIRECTINPUT8 di;
HRESULT hr;
LPDIRECTINPUTDEVICE8 wheel;

BOOL CALLBACK EnumCallback(const DIDEVICEINSTANCE* instance, VOID* context)
{
	HRESULT hr;

	// Obtain an interface to the enumerated wheel.
	hr = di->CreateDevice(instance->guidInstance, &wheel, NULL);

	// If it failed, then we can't use this wheel. (Maybe the user unplugged
	// it while we were in the middle of enumerating it.)
	if (FAILED(hr)) {
		return DIENUM_CONTINUE;
	}

	// Stop enumeration. Note: we're just taking the first wheel we get. You
	// could store all the enumerated wheels and let the user pick.
	return DIENUM_STOP;
}

int _tmain(int argc, _TCHAR* argv[])
{
	// Create a DirectInput device
	if (FAILED(hr = DirectInput8Create(GetModuleHandle(NULL), DIRECTINPUT_VERSION,
		IID_IDirectInput8, (VOID**)&di, NULL))) {
		return hr;
	}

	// Look for the first wheel we can find.
	if (FAILED(hr = di->EnumDevices(DI8DEVCLASS_GAMECTRL, EnumCallback,
		NULL, DIEDFL_ATTACHEDONLY))) {
		return hr;
	}

	// Make sure we got a wheel
	if (wheel == NULL) {
		printf("Wheel not found.\n");
		return E_FAIL;
	}

	// SDK Snippets
	{
		// Sample implementation of LogiSetOperatingRangeDInput only using part of the SDK.
		int range = 900;
		if (LogiSetOperatingRangeDInput(wheel, range))
		{
			printf("LogiSetOperatingRangeDInput with range = %d was successful.\n", range);
		}
		else
		{
			printf("LogiSetOperatingRangeDInput with range = %d failed.\n", range);
		}

		// Sample implementation of LogiGetOperatingRangeDInput only using part of the SDK.
		int newRange = 0;
		if (LogiGetOperatingRangeDInput(wheel, newRange))
		{
			printf("LogiSetOperatingRangeDInput returned newRange = %d.\n", newRange);
		}
		else
		{
			printf("LogiSetOperatingRangeDInput failed.\n");
		}

		// Sample implementation of LogiPlayLedsDInput only using part of the SDK.
		// Note: This snippet will fail due to the not having an active window handle.
		float currentRPM = 0.5;
		float rpmFirstLightOn = 0.1;
		float rpmRedLine = 1.0;
		if (LogiPlayLedsDInput(wheel, currentRPM, rpmFirstLightOn, rpmRedLine))
		{
			printf("LogiPlayLedsDInput was successful.\n", range);
		}
		else
		{
			printf("LogiPlayLedsDInput failed.\n", range);
		}
	}

	// No SDK Snippets
	{
		using namespace LogitechSteeringWheel;

		// Sample implementation of LogiSetOperatingRangeDInput only using a snippet.
		int range = 900;
		if (!FAILED(SetOperatingRangeDInput(wheel, range)))
		{
			printf("SetOperatingRangeDInput with range = %d was successful.\n", range);
		}
		else
		{
			printf("SetOperatingRangeDInput with range = %d failed.\n", range);
		}

		// Sample implementation of LogiGetOperatingRangeDInput only using a snippet.
		int newRange = 0;
		if (!FAILED(GetOperatingRangeDInput(wheel, newRange)))
		{
			printf("SetOperatingRangeDInput returned newRange = %d.\n", newRange);
		}
		else
		{
			printf("SetOperatingRangeDInput failed.\n");
		}

		// Sample implementation of LogiPlayLedsDInput only using a snippet.
		// Note: This snippet will fail due to the not having an active window handle.
		float currentRPM = 0.5;
		float rpmFirstLightOn = 0.1;
		float rpmRedLine = 1.0;
		if (!FAILED(PlayLedsDInput(wheel, currentRPM, rpmFirstLightOn, rpmRedLine)))
		{
			printf("PlayLedsDInput was successful.\n", range);
		}
		else
		{
			printf("PlayLedsDInput failed.\n", range);
		}
	}

	system("pause");
	return 0;
}

